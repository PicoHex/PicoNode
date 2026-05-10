# PicoNode

> A layered, AOT-native networking stack for .NET — from raw TCP/UDP sockets to a fully featured HTTP web framework.

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

**English** | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [日本語](README.ja.md) | [Português (Brasil)](README.pt-BR.md) | [Русский](README.ru.md)

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

## Why PicoNode?

| Feature | PicoNode | ASP.NET Core |
|---------|----------|-------------|
| **Dependency model** | Zero required runtime deps; layer pick-and-choose | `Microsoft.AspNetCore.App` framework reference |
| **Request parsing** | Span-based streaming, zero-copy `System.IO.Pipelines` | String-based with `IO.Pipelines` adapter |
| **HTTP/2** | Inline HPACK decoder, frame-level control | Transparent via Kestrel; limited low-level access |
| **AOT Support** | ✅ Native — all net10.0 libraries | ⚠️ Requires trimming |
| **DI / Logging / Config** | PicoDI + PicoLog + PicoCfg (PicoHex native) | Microsoft.Extensions.* |
| **WebSocket** | RFC 6455 frame codec with message handler abstraction | Transparent via middleware |
| **Line count** | ~15K for the full stack | ~1M+ for ASP.NET Core |

> **Design priority:** PicoNode prioritizes allocation efficiency and AOT compatibility. `ValueTask` on hot-path delegates, ArrayPool-based buffer management, and optional delegates (no forced allocations) are deliberate trade-offs — they keep the transport layer compact and predictable.

### The PicoHex Ecosystem

PicoNode is part of the PicoHex family and integrates natively with:

| Library | Purpose | NuGet |
|---------|---------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | Zero-reflection compile-time DI | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | Structured logging with AOT safety | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | Source-generated configuration binding | `PicoCfg.Abs` |

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

## Quick Start

### Installation

```bash
dotnet add package PicoNode
```

> Installing `PicoNode` brings in the TCP/UDP transport. Reference `PicoNode.Http` or `PicoNode.Web` for higher-level layers.

### Package Architecture

PicoNode ships as layered NuGet packages. Pick exactly the abstraction level you need:

| Package | Install when… | What you get |
|---------|--------------|-------------|
| **PicoWeb** | You want a ready-to-run web server | WebServer + WebApp + HTTP + TCP (all transitive) |
| **PicoNode.Web** | You want the web framework without hosting | WebApp, routing, middleware, static files, DI |
| **PicoNode.Http** | You want raw HTTP protocol handling | HTTP/1.1 + HTTP/2 + WebSocket, HttpRouter |
| **PicoNode** | You want raw TCP/UDP transports | TcpNode, UdpNode, socket lifecycle, metrics |
| **PicoNode.Abs** | You're writing a handler or extension | INode, ITcpConnectionHandler, core contracts |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(host)      (web/DI)         (HTTP)            (transport)   (interfaces)
```

### TCP Echo Server

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

### HTTP Server (Low-Level)

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

### Web Application (with PicoHex Ecosystem)

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

## Configuration

PicoNode supports two configuration modes:

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

### PicoCfg Binding (AOT-safe, source-generated)

```csharp
var config = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var options = CfgBind.Bind<TcpNodeOptions>(config, "TcpNode");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // required
var node = new TcpNode(options);
```

### Runtime Reload

```csharp
// TcpNode supports runtime config reload (except Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // ICfgRoot for live reload
};
// Node starts a reload loop watching for config changes
```

### Key Options

#### TcpNodeOptions

| Option | Default | Description |
|--------|---------|-------------|
| `Endpoint` | *(required)* | Local endpoint to bind |
| `ConnectionHandler` | *(required)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | Maximum concurrent connections |
| `IdleTimeout` | 2 min | Time before idle connections are closed |
| `DrainTimeout` | 5 sec | Grace period on shutdown |
| `SslOptions` | `null` | TLS/SSL configuration |
| `NoDelay` | `true` | TCP_NODELAY (Nagle disabled) |
| `Logger` | `null` | PicoLog `ILogger` for structured diagnostics |

#### UdpNodeOptions

| Option | Default | Description |
|--------|---------|-------------|
| `Endpoint` | *(required)* | Local endpoint to bind |
| `DatagramHandler` | *(required)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | Concurrent datagram workers |
| `DatagramQueueCapacity` | 1024 | Per-worker queue depth |
| `QueueOverflowMode` | `DropNewest` | Behavior when queues are full |
| `Logger` | `null` | PicoLog `ILogger` |

#### HttpConnectionHandlerOptions

| Option | Default | Description |
|--------|---------|-------------|
| `RequestHandler` | *(required)* | HttpRequestHandler delegate |
| `ServerHeader` | `null` | Value for the `Server` header |
| `MaxRequestBytes` | 8192 | Maximum request size in bytes |
| `Logger` | `null` | PicoLog `ILogger` |

## Logging

PicoNode uses PicoLog for structured diagnostics. All non-fatal errors are logged with operation context:

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

**Log levels by fault code:**
- `Error`: StartFailed, StopFailed, AcceptFailed, ReceiveFailed, SendFailed, HandlerFailed, TlsFailed, DatagramReceiveFailed, DatagramHandlerFailed
- `Warning`: SessionRejected, DatagramDropped
- `Debug`: Socket shutdown during cleanup (best-effort operations)

## Dependency Injection

PicoNode's Web layer integrates with PicoDI for scoped request handling:

```csharp
var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();
container.RegisterSingleton<ICache, RedisCache>();

var app = new WebApp();
app.Build(container); // Injects scope middleware per request

// In your route handler:
app.MapGet("/db", async (ctx, ct) =>
{
    var db = ctx.Services!.GetService<IDatabase>();
    var data = await db.QueryAsync("...");
    return WebResults.Json(200, data);
});
```

## Built-in Middleware

### Compression

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Supports Brotli, Gzip, and Deflate. Auto-selects the best encoding from the client's `Accept-Encoding` header.

### Static Files

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

Serves files from a root directory. Prevents directory traversal. Maps 30+ file extensions to MIME types.

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

## Metrics

Both `TcpNode` and `UdpNode` expose real-time counters:

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

## Projects

| Project | Target | Description |
|---------|--------|-------------|
| **PicoNode.Abs** | netstandard2.0 | Core interfaces: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, fault codes, enums |
| **PicoNode** | net10.0 | `TcpNode` and `UdpNode` — production-grade async socket transports |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, middleware, static files, compression, CORS, DI |
| **PicoWeb** | net10.0 | `WebServer` — thin host wiring `WebApp` to `TcpNode` |

## Samples

| Sample | Port | Description |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Raw TCP/UDP echo server |
| `PicoNode.Samples.Http` | 7003 | HTTP routing with `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Full web app with middleware and DI |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Building & Testing

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

Microbenchmarks are provided via [PicoBench](https://github.com/PicoHex/PicoBench):

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

Benchmarks cover HTTP parsing, router dispatch (hit/miss/405), full pipeline, and localhost round-trips.

## Requirements

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — maximum compatibility)
- PicoHex ecosystem (optional): PicoDI, PicoLog, PicoCfg

## License

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — layered networking for .NET
</p>
