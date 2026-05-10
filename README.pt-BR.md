# PicoNode

> Uma pilha de rede em camadas, nativa AOT para .NET — de sockets TCP/UDP brutos a um framework web HTTP completo.

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

**English** | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [日本語](README.ja.md) | **Português (Brasil)** | [Русский](README.ru.md)

```
┌─────────────────────────────────────────────────────────────┐
│  PicoNode: redes em camadas para .NET                       │
│  ✓ Transporte TCP/UDP bruto com I/O assíncrona              │
│  ✓ Protocolos HTTP/1.1 + HTTP/2 + WebSocket                 │
│  ✓ Framework web com middleware, roteamento, arquivos estáticos│
│  ✓ Integrado ao ecossistema PicoHex (PicoDI/PicoLog/PicoCfg) │
│  ✓ Compatível com AOT nativo em todas as camadas net10.0     │
│  ✓ Dependências mínimas em tempo de execução                 │
└─────────────────────────────────────────────────────────────┘
```

## Por que PicoNode?

| Característica | PicoNode | ASP.NET Core |
|----------------|----------|-------------|
| **Modelo de dependências** | Zero dependências obrigatórias; escolha a camada | Referência ao `Microsoft.AspNetCore.App` |
| **Análise de requisições** | Streaming baseado em Span, zero-copy com `System.IO.Pipelines` | Baseado em string com adaptador `IO.Pipelines` |
| **HTTP/2** | Decodificador HPACK embutido, controle de nível de frame | Transparente via Kestrel; acesso limitado a baixo nível |
| **Suporte AOT** | ✅ Nativo — todas as bibliotecas net10.0 | ⚠️ Requer trimming |
| **DI / Log / Config** | PicoDI + PicoLog + PicoCfg (nativos PicoHex) | Microsoft.Extensions.* |
| **WebSocket** | Codec de frame RFC 6455 com abstração de handler de mensagem | Transparente via middleware |
| **Linhas de código** | ~15K para a pilha completa | ~1M+ para ASP.NET Core |

> **Prioridade de projeto:** O PicoNode prioriza eficiência de alocação e compatibilidade AOT. `ValueTask` em delegates de hot-path, gerenciamento de buffers baseado em ArrayPool e delegates opcionais (sem alocações forçadas) são escolhas deliberadas — elas mantêm a camada de transporte compacta e previsível.

### O Ecossistema PicoHex

O PicoNode faz parte da família PicoHex e se integra nativamente com:

| Biblioteca | Propósito | NuGet |
|------------|-----------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | DI em tempo de compilação sem reflexão | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | Log estruturado com segurança AOT | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | Vinculação de configuração gerada por fonte | `PicoCfg.Abs` |

```
PicoNode.Abs        Interfaces centrais                       (netstandard2.0, zero deps)
    ↓
PicoNode             Transportes TCP & UDP + ILogger           (net10.0)
    ↓
PicoNode.Http        HTTP/1.1 + HTTP/2 + WebSocket             (net10.0)
    ↓
PicoNode.Web         Framework web + PicoDI ISvcContainer      (net10.0)
    ↓
PicoWeb              Servidor web pronto para execução + PicoCfg (net10.0)
```

## Início Rápido

### Instalação

```bash
dotnet add package PicoNode
```

> Instalar o `PicoNode` traz o transporte TCP/UDP. Referencie `PicoNode.Http` ou `PicoNode.Web` para camadas de nível superior.

### Arquitetura de Pacotes

O PicoNode é distribuído como pacotes NuGet em camadas. Escolha exatamente o nível de abstração que você precisa:

| Pacote | Instale quando… | O que você obtém |
|---------|----------------|-----------------|
| **PicoWeb** | Você quer um servidor web pronto para execução | WebServer + WebApp + HTTP + TCP (tudo transitivo) |
| **PicoNode.Web** | Você quer o framework web sem hospedagem | WebApp, roteamento, middleware, arquivos estáticos, DI |
| **PicoNode.Http** | Você quer manipulação de protocolo HTTP bruta | HTTP/1.1 + HTTP/2 + WebSocket, HttpRouter |
| **PicoNode** | Você quer transportes TCP/UDP brutos | TcpNode, UdpNode, ciclo de vida de sockets, métricas |
| **PicoNode.Abs** | Você está escrevendo um handler ou extensão | INode, ITcpConnectionHandler, contratos centrais |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(host)      (web/DI)         (HTTP)            (transporte)  (interfaces)
```

### Servidor TCP Echo

```csharp
using System.Net;
using PicoNode;
using PicoNode.Abs;

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
    ConnectionHandler = new EchoHandler(),
});

await node.StartAsync();
Console.ReadLine();
await node.DisposeAsync();

sealed class EchoHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(ITcpConnectionContext c, CancellationToken ct)
        => Task.CompletedTask;
    public Task OnClosedAsync(ITcpConnectionContext c, TcpCloseReason r,
        Exception? e, CancellationToken ct) => Task.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken ct)
    {
        _ = connection.SendAsync(buffer, ct);
        return ValueTask.FromResult(buffer.End);
    }
}
```

### Servidor HTTP (Baixo Nível)

```csharp
using System.Net;
using PicoNode;
using PicoNode.Http;

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7002),
    ConnectionHandler = new HttpConnectionHandler(new HttpConnectionHandlerOptions
    {
        RequestHandler = new HttpRouter(new HttpRouterOptions
        {
            Routes =
            [
                HttpRoute.MapGet("/", static (_, _) =>
                    ValueTask.FromResult(new HttpResponse
                    {
                        StatusCode = 200, ReasonPhrase = "OK",
                        Headers = [new("Content-Type", "text/plain")],
                        Body = "Hello from PicoNode.Http"u8.ToArray(),
                    })),
            ],
        }).HandleAsync,
        ServerHeader = "PicoNode",
    }),
});

await node.StartAsync();
Console.ReadLine();
await node.DisposeAsync();
```

### Aplicação Web (com Ecossistema PicoHex)

```csharp
using System.Net;
using PicoDI.Abs;
using PicoLog.Abs;
using PicoCfg.Abs;
using PicoNode.Web;
using PicoWeb;

// Configuração
var config = await Cfg.CreateBuilder()
    .Add(new Dictionary<string, string>
    {
        ["WebApp:ServerHeader"] = "MyApp",
        ["WebApp:MaxRequestBytes"] = "16384",
    })
    .BuildAsync();

var app = new WebApp(new WebAppOptions
{
    ServerHeader = "MyApp",
    Logger = new ConsoleSink().CreateLogger("PicoNode.Web"),
    Config = config,
});

// Middleware
app.Use(async (context, next, ct) =>
{
    var response = await next(context, ct);
    return response;
});

// Rotas
app.MapGet("/", static (_, _) =>
    ValueTask.FromResult(WebResults.Text(200, "Hello, World!", "OK")));

app.MapGet("/users/{id}", static (ctx, _) =>
{
    var id = ctx.RouteValues["id"];
    return ValueTask.FromResult(
        WebResults.Json(200, $$"""{"id":"{{id}}"}""", "OK"));
});

app.MapPost("/echo", static (ctx, _) =>
{
    var body = Encoding.UTF8.GetString(ctx.Request.Body.Span);
    return ValueTask.FromResult(WebResults.Text(200, body, "OK"));
});

// Hospedagem com DI
var container = new SvcContainer();
container.RegisterSingleton<IMyService, MyServiceImpl>();

await using var server = new WebServer(app, new WebServerOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
}, container);

await server.StartAsync();
Console.ReadLine();
await server.StopAsync();
```

## Configuração

O PicoNode suporta dois modos de configuração:

### Código-Primeiro (inline)

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### Vinculação PicoCfg (segura para AOT, gerada por fonte)

```csharp
var config = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var options = CfgBind.Bind<TcpNodeOptions>(config, "TcpNode");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // obrigatório
var node = new TcpNode(options);
```

### Recarga em Tempo de Execução

```csharp
// TcpNode suporta recarga de configuração em execução (exceto Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // ICfgRoot para recarga ao vivo
};
// O nó inicia um loop de recarga monitorando mudanças na configuração
```

### Opções Principais

#### TcpNodeOptions

| Opção | Padrão | Descrição |
|-------|--------|-----------|
| `Endpoint` | *(obrigatório)* | Endpoint local para vincular |
| `ConnectionHandler` | *(obrigatório)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | Máximo de conexões simultâneas |
| `IdleTimeout` | 2 min | Tempo antes de fechar conexões ociosas |
| `DrainTimeout` | 5 s | Período de graça no desligamento |
| `SslOptions` | `null` | Configuração TLS/SSL |
| `NoDelay` | `true` | TCP_NODELAY (Nagle desabilitado) |
| `Logger` | `null` | PicoLog `ILogger` para diagnósticos estruturados |

#### UdpNodeOptions

| Opção | Padrão | Descrição |
|-------|--------|-----------|
| `Endpoint` | *(obrigatório)* | Endpoint local para vincular |
| `DatagramHandler` | *(obrigatório)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | Workers de datagramas simultâneos |
| `DatagramQueueCapacity` | 1024 | Profundidade da fila por worker |
| `QueueOverflowMode` | `DropNewest` | Comportamento quando as filas estão cheias |
| `Logger` | `null` | PicoLog `ILogger` |

#### HttpConnectionHandlerOptions

| Opção | Padrão | Descrição |
|-------|--------|-----------|
| `RequestHandler` | *(obrigatório)* | Delegate HttpRequestHandler |
| `ServerHeader` | `null` | Valor para o cabeçalho `Server` |
| `MaxRequestBytes` | 8192 | Tamanho máximo da requisição em bytes |
| `Logger` | `null` | PicoLog `ILogger` |

## Log

O PicoNode usa PicoLog para diagnósticos estruturados. Todos os erros não fatais são registrados com contexto da operação:

```csharp
var logger = new LoggerFactory([new ConsoleSink()])
    .CreateLogger("PicoNode.Tcp");

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
    ConnectionHandler = handler,
    Logger = logger, // Todas as falhas de transporte são registradas aqui
});

// Saída de log:
// [Error] Operation tcp.accept failed: AcceptFailed - System.Net.Sockets.SocketException
// [Warning] Operation tcp.reject.limit failed: SessionRejected
// [Debug] Socket shutdown during TLS teardown failed
```

**Níveis de log por código de falha:**
- `Error`: StartFailed, StopFailed, AcceptFailed, ReceiveFailed, SendFailed, HandlerFailed, TlsFailed, DatagramReceiveFailed, DatagramHandlerFailed
- `Warning`: SessionRejected, DatagramDropped
- `Debug`: Socket shutdown durante limpeza (operações de melhor esforço)

## Injeção de Dependência

A camada Web do PicoNode se integra com PicoDI para manipulação de requisições com escopo:

```csharp
var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();
container.RegisterSingleton<ICache, RedisCache>();

var app = new WebApp();
app.Build(container); // Injeta middleware de escopo por requisição

// No seu handler de rota:
app.MapGet("/db", async (ctx, ct) =>
{
    var db = ctx.Services!.GetService<IDatabase>();
    var data = await db.QueryAsync("...");
    return WebResults.Json(200, data);
});
```

## Middleware Embutido

### Compressão

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Suporta Brotli, Gzip e Deflate. Seleciona automaticamente a melhor codificação a partir do cabeçalho `Accept-Encoding` do cliente.

### Arquivos Estáticos

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

Serve arquivos de um diretório raiz. Previne directory traversal. Mapeia mais de 30 extensões de arquivo para tipos MIME.

### CORS

```csharp
app.Use((ctx, next, ct) =>
{
    var corsOptions = new CorsOptions
    {
        AllowedOrigins = ["https://example.com"],
        AllowedMethods = ["GET", "POST"],
        AllowCredentials = true,
    };
    var preflight = CorsHandler.HandlePreflight(ctx.Request, corsOptions);
    if (preflight is not null)
        return ValueTask.FromResult(preflight);
    var response = await next(ctx, ct);
    CorsHandler.ApplyResponseHeaders(response, corsOptions);
    return response;
});
```

### Cookies & Multipart

```csharp
// Análise de cookies
var cookies = CookieParser.Parse(context.Request.HeaderFields);

// Set-Cookie
var setCookie = new SetCookieBuilder("session", "abc123")
    .Path("/").HttpOnly().Secure().SameSite("Strict").MaxAge(3600)
    .Build();

// Dados de formulário multipart
var form = MultipartFormDataParser.Parse(context.Request);
foreach (var field in form?.Fields ?? [])
    Console.WriteLine($"{field.Name} = {field.Value}");
foreach (var file in form?.Files ?? [])
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Data.Length} bytes)");
```

## Métricas

Tanto `TcpNode` quanto `UdpNode` expõem contadores em tempo real:

```csharp
// TCP
var tcpMetrics = tcpNode.GetMetrics();
Console.WriteLine($"Accepted: {tcpMetrics.TotalAccepted}");
Console.WriteLine($"Active: {tcpMetrics.ActiveConnections}");
Console.WriteLine($"Sent: {tcpMetrics.TotalBytesSent}");
Console.WriteLine($"Received: {tcpMetrics.TotalBytesReceived}");

// UDP
var udpMetrics = udpNode.GetMetrics();
Console.WriteLine($"Datagrams Rx: {udpMetrics.TotalDatagramsReceived}");
Console.WriteLine($"Datagrams Tx: {udpMetrics.TotalDatagramsSent}");
Console.WriteLine($"Dropped: {udpMetrics.TotalDropped}");
```

## Projetos

| Projeto | Alvo | Descrição |
|---------|------|-----------|
| **PicoNode.Abs** | netstandard2.0 | Interfaces centrais: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, códigos de falha, enums |
| **PicoNode** | net10.0 | `TcpNode` e `UdpNode` — transportes de socket assíncronos de nível de produção |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, middleware, arquivos estáticos, compressão, CORS, DI |
| **PicoWeb** | net10.0 | `WebServer` — hospedagem fina conectando `WebApp` ao `TcpNode` |

## Exemplos

| Exemplo | Porta | Descrição |
|---------|-------|-----------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Servidor echo TCP/UDP bruto |
| `PicoNode.Samples.Http` | 7003 | Roteamento HTTP com `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Aplicação web completa com middleware e DI |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Compilação & Testes

```bash
# Compilar a solução inteira
dotnet build PicoNode.slnx -c Release

# Executar todos os testes
dotnet test --solution PicoNode.slnx -c Release

# Executar um projeto de teste específico
dotnet test --project tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release

# Verificação de publicação AOT
dotnet publish src/PicoWeb/PicoWeb.csproj -c Release -r win-x64 -p:PublishAot=true
```

## Benchmarks

Microbenchmarks são fornecidos via [PicoBench](https://github.com/PicoHex/PicoBench):

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Os benchmarks cobrem análise HTTP, despacho de roteador (acerto/erro/405), pipeline completo e viagens de ida e volta em localhost.

## Requisitos

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — compatibilidade máxima)
- Ecossistema PicoHex (opcional): PicoDI, PicoLog, PicoCfg

## Licença

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — redes em camadas para .NET
</p>
