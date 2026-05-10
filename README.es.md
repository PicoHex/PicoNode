# PicoNode

> Un stack de networking nativo AOT y por capas para .NET — desde sockets TCP/UDP puros hasta un framework web HTTP completo.

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

[English](README.md) | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | **Español** | [Français](README.fr.md) | [日本語](README.ja.md) | [Português (Brasil)](README.pt-BR.md) | [Русский](README.ru.md)

```
┌─────────────────────────────────────────────────────────────┐
│  PicoNode: layered networking for .NET                      │
│  ✓ Raw TCP/UDP socket transports with async I/O             │
│  ✓ HTTP/1.1 + HTTP/2 + WebSocket protocols                  │
│  ✓ Web framework with middleware, routing, static files      │
│  ✓ Integrated with PicoHex ecosystem (PicoDI/PicoLog/PicoCfg)│
│  ✓ Native AOT compatible across all net10.0 layers           │
│  ✓ Minimal runtime dependencies                             │
└─────────────────────────────────────────────────────────────┘
```

## ¿Por qué PicoNode?

| Característica | PicoNode | ASP.NET Core |
|---------|----------|-------------|
| **Modelo de dependencias** | Sin dependencias de ejecución obligatorias; capas seleccionables | Referencia al framework `Microsoft.AspNetCore.App` |
| **Análisis de solicitudes** | Streaming basado en Span, `System.IO.Pipelines` zero-copy | Basado en strings con adaptador `IO.Pipelines` |
| **HTTP/2** | Decodificador HPACK inline, control a nivel de trama | Transparente vía Kestrel; acceso limitado a bajo nivel |
| **Compatibilidad AOT** | ✅ Nativo — todas las librerías net10.0 | ⚠️ Requiere trimming |
| **DI / Logging / Config** | PicoDI + PicoLog + PicoCfg (nativos de PicoHex) | Microsoft.Extensions.* |
| **WebSocket** | Códec de trama RFC 6455 con abstracción de manejador de mensajes | Transparente vía middleware |
| **Líneas de código** | ~15K para todo el stack | ~1M+ para ASP.NET Core |

> **Prioridad de diseño:** PicoNode prioriza la eficiencia de asignación y la compatibilidad AOT. El uso de `ValueTask` en delegados de ruta crítica, la gestión de búferes basada en ArrayPool y los delegados opcionales (sin asignaciones forzadas) son compensaciones deliberadas — mantienen la capa de transporte compacta y predecible.

### El Ecosistema PicoHex

PicoNode es parte de la familia PicoHex y se integra de forma nativa con:

| Librería | Propósito | NuGet |
|---------|---------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | DI en tiempo de compilación sin reflection | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | Logging estructurado con seguridad AOT | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | Vinculación de configuración generada en código fuente | `PicoCfg.Abs` |

```
PicoNode.Abs        Core interfaces                          (netstandard2.0, zero deps)
    ↓
PicoNode             TCP & UDP transports + ILogger           (net10.0)
    ↓
PicoNode.Http        HTTP/1.1 + HTTP/2 + WebSocket            (net10.0)
    ↓
PicoNode.Web         Web framework + PicoDI ISvcContainer     (net10.0)
    ↓
PicoWeb              Ready-to-run web server + PicoCfg        (net10.0)
```

## Inicio Rápido

### Instalación

```bash
dotnet add package PicoNode
```

> Al instalar `PicoNode` se incluye el transporte TCP/UDP. Referencia `PicoNode.Http` o `PicoNode.Web` para capas de nivel superior.

### Arquitectura de Paquetes

PicoNode se distribuye como paquetes NuGet por capas. Elige exactamente el nivel de abstracción que necesites:

| Paquete | Instalar cuando… | Qué obtienes |
|---------|--------------|-------------|
| **PicoWeb** | Quieres un servidor web listo para ejecutar | WebServer + WebApp + HTTP + TCP (todo transitivo) |
| **PicoNode.Web** | Quieres el framework web sin hosting | WebApp, routing, middleware, archivos estáticos, DI |
| **PicoNode.Http** | Quieres manejo de protocolo HTTP puro | HTTP/1.1 + HTTP/2 + WebSocket, HttpRouter |
| **PicoNode** | Quieres transportes TCP/UDP puros | TcpNode, UdpNode, ciclo de vida de sockets, métricas |
| **PicoNode.Abs** | Estás escribiendo un manejador o extensión | INode, ITcpConnectionHandler, contratos principales |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(host)      (web/DI)         (HTTP)            (transport)   (interfaces)
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

### Servidor HTTP (Bajo Nivel)

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

### Aplicación Web (con el Ecosistema PicoHex)

```csharp
using System.Net;
using PicoDI.Abs;
using PicoLog.Abs;
using PicoCfg.Abs;
using PicoNode.Web;
using PicoWeb;

// Configuration
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

// Routes
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

// DI-aware hosting
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

## Configuración

PicoNode admite dos modos de configuración:

### Code-First (inline)

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### Vinculación PicoCfg (seguro para AOT, generado en código fuente)

```csharp
var config = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var options = CfgBind.Bind<TcpNodeOptions>(config, "TcpNode");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // required
var node = new TcpNode(options);
```

### Recarga en Tiempo de Ejecución

```csharp
// TcpNode soporta recarga de configuración en tiempo de ejecución (excepto Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // ICfgRoot para recarga en vivo
};
// El nodo inicia un bucle de recarga que observa cambios en la configuración
```

### Opciones Clave

#### TcpNodeOptions

| Opción | Por Defecto | Descripción |
|--------|-------------|-------------|
| `Endpoint` | *(obligatorio)* | Endpoint local al que vincularse |
| `ConnectionHandler` | *(obligatorio)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | Máximo de conexiones concurrentes |
| `IdleTimeout` | 2 min | Tiempo antes de cerrar conexiones inactivas |
| `DrainTimeout` | 5 seg | Período de gracia al apagar |
| `SslOptions` | `null` | Configuración TLS/SSL |
| `NoDelay` | `true` | TCP_NODELAY (Nagle desactivado) |
| `Logger` | `null` | `ILogger` de PicoLog para diagnóstico estructurado |

#### UdpNodeOptions

| Opción | Por Defecto | Descripción |
|--------|-------------|-------------|
| `Endpoint` | *(obligatorio)* | Endpoint local al que vincularse |
| `DatagramHandler` | *(obligatorio)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | Trabajadores de datagramas concurrentes |
| `DatagramQueueCapacity` | 1024 | Profundidad de cola por trabajador |
| `QueueOverflowMode` | `DropNewest` | Comportamiento cuando las colas están llenas |
| `Logger` | `null` | `ILogger` de PicoLog |

#### HttpConnectionHandlerOptions

| Opción | Por Defecto | Descripción |
|--------|-------------|-------------|
| `RequestHandler` | *(obligatorio)* | Delegado HttpRequestHandler |
| `ServerHeader` | `null` | Valor para la cabecera `Server` |
| `MaxRequestBytes` | 8192 | Tamaño máximo de solicitud en bytes |
| `Logger` | `null` | `ILogger` de PicoLog |

## Logging

PicoNode usa PicoLog para diagnóstico estructurado. Todos los errores no fatales se registran con contexto de operación:

```csharp
var logger = new LoggerFactory([new ConsoleSink()])
    .CreateLogger("PicoNode.Tcp");

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
    ConnectionHandler = handler,
    Logger = logger, // All transport faults logged here
});

// Log output:
// [Error] Operation tcp.accept failed: AcceptFailed - System.Net.Sockets.SocketException
// [Warning] Operation tcp.reject.limit failed: SessionRejected
// [Debug] Socket shutdown during TLS teardown failed
```

**Niveles de log según código de fallo:**
- `Error`: StartFailed, StopFailed, AcceptFailed, ReceiveFailed, SendFailed, HandlerFailed, TlsFailed, DatagramReceiveFailed, DatagramHandlerFailed
- `Warning`: SessionRejected, DatagramDropped
- `Debug`: Socket shutdown durante la limpieza (operaciones de mejor esfuerzo)

## Inyección de Dependencias

La capa Web de PicoNode se integra con PicoDI para el manejo de solicitudes con ámbito (scoped):

```csharp
var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();
container.RegisterSingleton<ICache, RedisCache>();

var app = new WebApp();
app.Build(container); // Inyecta middleware de ámbito por solicitud

// En tu manejador de ruta:
app.MapGet("/db", async (ctx, ct) =>
{
    var db = ctx.Services!.GetService<IDatabase>();
    var data = await db.QueryAsync("...");
    return WebResults.Json(200, data);
});
```

## Middleware Integrado

### Compresión

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Soporta Brotli, Gzip y Deflate. Selecciona automáticamente la mejor codificación según la cabecera `Accept-Encoding` del cliente.

### Archivos Estáticos

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

Sirve archivos desde un directorio raíz. Previene el directory traversal. Asigna más de 30 extensiones de archivo a tipos MIME.

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

### Cookies y Multipart

```csharp
// Cookie parsing
var cookies = CookieParser.Parse(context.Request.HeaderFields);

// Set-Cookie
var setCookie = new SetCookieBuilder("session", "abc123")
    .Path("/").HttpOnly().Secure().SameSite("Strict").MaxAge(3600)
    .Build();

// Multipart form data
var form = MultipartFormDataParser.Parse(context.Request);
foreach (var field in form?.Fields ?? [])
    Console.WriteLine($"{field.Name} = {field.Value}");
foreach (var file in form?.Files ?? [])
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Data.Length} bytes)");
```

## Métricas

Tanto `TcpNode` como `UdpNode` exponen contadores en tiempo real:

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

## Proyectos

| Proyecto | Target | Descripción |
|---------|--------|-------------|
| **PicoNode.Abs** | netstandard2.0 | Interfaces principales: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, códigos de fallo, enums |
| **PicoNode** | net10.0 | `TcpNode` y `UdpNode` — transportes de socket asíncronos de calidad de producción |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, middleware, archivos estáticos, compresión, CORS, DI |
| **PicoWeb** | net10.0 | `WebServer` — host delgado que conecta `WebApp` con `TcpNode` |

## Ejemplos

| Ejemplo | Puerto | Descripción |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Servidor echo TCP/UDP puro |
| `PicoNode.Samples.Http` | 7003 | Enrutamiento HTTP con `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Aplicación web completa con middleware y DI |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Compilación y Pruebas

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

Los microbenchmarks se proporcionan a través de [PicoBench](https://github.com/PicoHex/PicoBench):

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Los benchmarks cubren análisis HTTP, despacho de router (acierto/fallo/405), pipeline completo y viajes de ida y vuelta en localhost.

## Requisitos

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — máxima compatibilidad)
- Ecosistema PicoHex (opcional): PicoDI, PicoLog, PicoCfg

## Licencia

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — networking por capas para .NET
</p>
