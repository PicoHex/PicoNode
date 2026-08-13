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

### Aplicação Web (DI Primeiro + Delegado)

```csharp
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .ConfigureApp(_ => new WebAppOptions { ServerHeader = "MyApp" })
    // ConfigureApp receives the CURRENT options — later calls can build on
    // earlier configuration instead of starting from defaults.
    .RegisterScoped<IUserService, UserService>()
    .Build();

api.MapGet("/", (WebContext ctx) =>
    Results.Text(200, "Hello, World!"));

api.MapGet("/users/{id}", async (WebContext ctx, IUserService svc) =>
{
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});

api.MapPost("/echo", async (WebContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.BodyStream);
    var body = await reader.ReadToEndAsync();
    return Results.Text(200, body);
});

await api.RunAsync("http://+:8080");
```

### Aplicação Web (Baseada em Controllers)

```csharp
// Controllers/UsersController.cs
using PicoJetson;

public class UsersController
{
    public UserDto GetUser(int id) { return new UserDto { Id = id }; }
}

// Program.cs
var api = new WebApiBuilder()
    .RegisterScoped<UsersController>()
    .Build();

// Controllers.Gen auto-generates endpoint stubs + [PicoJsonSerializable]
await api.RunAsync("http://+:8080");
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

var settings = CfgBind.Bind<AppSettings>(config, "App");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // obrigatório
var node = new TcpNode(options);
```

### Recarga em Tempo de Execução

```csharp
// TcpNode suporta recarga de configuração em execução (exceto Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
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

O PicoNode.Web exige `ISvcContainer` no momento da construção (DI primeiro). Escopos são criados automaticamente por requisição.

### Resolução manual de DI nos handlers

```csharp
using PicoNode.Web;
using PicoWeb;
using PicoJetson;

var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();

var app = new WebApp(container);
app.MapGet("/db", async (WebContext ctx) =>
{
    var db = ctx.Services.GetService<IDatabase>() as IDatabase;
    var data = await db!.QueryAsync("...");
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(data);
    return Results.Json(200, bytes);
});

app.Build();
```

### Injeção automática de parâmetros via Delegate

Os parâmetros do handler são resolvidos automaticamente (requer `using PicoNode.Web;`):
- `WebContext` → contexto atual
- `CancellationToken` → token de cancelamento da requisição
- Qualquer serviço registrado → resolvido a partir do escopo DI

```csharp
app.MapGet("/users/{id}", async (WebContext ctx, IUserService svc) =>
{
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});
```

### Serialização compatível com AOT

Os geradores de código-fonte do PicoJetson rodam em tempo de compilação. Os handlers devem chamar `SerializeToUtf8Bytes<T>()` diretamente no código do usuário para acionar o gerador:

```csharp
// ✅ Triggers PicoJetson.Gen — UserDto serializer generated
var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);

// ❌ Does NOT trigger generator (cross-assembly generic)
Results.Json<UserDto>(200, user);
```

### WebApiBuilder (conveniência)

```csharp
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .RegisterScoped<IUserService, UserService>()
    .ConfigureJson(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    .Build();

api.MapGet("/api/users/{id}", async (WebContext ctx, IUserService svc) =>
{
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});

await api.RunAsync("http://+:5000");
```

### WebApiBuilder com Controllers (registro de endpoints em três etapas)

```csharp
// 1. Controller in Controllers/ folder (convention)
//    Controllers/UsersController.cs
public class UsersController
{
    public UserDto GetUser(int id) { return new UserDto { ... }; }
    public List<UserDto> GetAllUsers() { return ...; }
}

// 2. Register controller in DI
builder.RegisterScoped<UsersController>();

// 3. Call EndpointRegistrar (auto-generated by Controllers.Gen)
EndpointRegistrar.RegisterAll(app);

// 4. Or use WebApiBuilder (calls it automatically)
new WebApiBuilder()
    .RegisterScoped<UsersController>()
    .Build()
    .RunAsync("http://+:5000");
```

Geradores de código-fonte Controllers.Gen e PicoWeb.Gen:
- Escaneiam a pasta `Controllers/` e as chamadas `app.MapGet/MapPost`
- Geram `[PicoJsonSerializable]` para os DTOs descobertos
- Geram stubs de endpoint que resolvem controllers a partir do DI

> **Nota:** o padrão baseado em controllers requer PicoJetson.Gen para o registro automático de serialização de DTOs.
> Para o padrão MapXX, chame `PicoJetson.JsonSerializer.SerializeToUtf8Bytes<T>()` explicitamente no handler.
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
        return preflight;
    var response = await next(ctx, ct);
    // Add CORS response headers
    foreach (var header in CorsHandler.GetResponseHeaders(ctx.Request, corsOptions))
    {
        response.Headers.Add(header.Key, header.Value);
    }
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
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Content.Length bytes)");
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
// UDP counters available via internal state
// (UdpNode tracks datagrams, bytes, and drops internally)
```

## Projetos

| Project | Target | Description |
|---------|--------|-------------|
| **PicoNode.Abs** | netstandard2.0 | Interfaces principais: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, códigos de falha, enums |
| **PicoNode** | net10.0 | `TcpNode` e `UdpNode` — transportes de socket assíncronos de nível de produção |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, middleware, arquivos estáticos, compressão, CORS, DI |
| **PicoWeb** | net10.0 | `WebServer` — host fino que conecta `WebApp` ao `TcpNode` |

## Exemplos

| Sample | Port | Description |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Servidor echo TCP/UDP puro |
| `PicoNode.Samples.Http` | 7003 | Roteamento HTTP com `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Aplicação web completa com middleware e DI |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Build e Testes

```bash
# Build the entire solution
dotnet build PicoNode.slnx -c Release

# Run all tests
dotnet test --solution PicoNode.slnx -c Release

# Run a specific test project
dotnet test --project tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release

# AOT publish check
dotnet publish src/PicoWeb/PicoWeb.csproj -c Release -r win-x64 -p:PublishAot=true
```

## Benchmarks

Microbenchmarks são fornecidos via [PicoBench](https://github.com/PicoHex/PicoBench):

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Os benchmarks cobrem análise de HTTP, despacho do roteador (acerto/erro/405), pipeline completo e round-trips em localhost.

## Requisitos

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — compatibilidade máxima)
- PicoHex ecosystem (opcional): PicoDI, PicoLog, PicoCfg

## Licença

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — rede em camadas para .NET
</p>
