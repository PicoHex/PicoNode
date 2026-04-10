# PicoNode

A lightweight, high-performance networking stack for .NET — from raw TCP/UDP sockets to a fully featured HTTP web framework, with zero external runtime dependencies.

## Overview

PicoNode is a layered networking library built from the ground up in modern C#. Each layer is independently usable, so you can pick exactly the abstraction level you need:

```
PicoNode.Abs        Core interfaces                          (netstandard2.0)
    ↓
PicoNode             TCP & UDP transports                     (net10.0)
    ↓
PicoNode.Http        HTTP/1.1 protocol handler                (net10.0)
    ↓
PicoNode.Web         Web framework (routing, middleware, …)   (net10.0)
    ↓
PicoWeb              Ready-to-run web server                  (net10.0)
```

## Features

- **TCP server** — async socket I/O via `System.IO.Pipelines`, configurable connection limits, idle timeout, TLS/SSL, graceful drain on shutdown
- **UDP server** — multi-worker queue dispatch, multicast & broadcast, configurable overflow policy (drop / wait)
- **HTTP/1.1** — streaming request parsing, persistent connections, `100-Continue`, chunked transfer encoding, configurable max request size
- **Web framework** — fluent `WebApp` builder, middleware pipeline, parameterized route patterns (`/users/{id}`), response helpers for text/JSON/redirect
- **Built-in middleware** — response compression (Brotli, Gzip, Deflate), static file serving, CORS, cookie parsing, multipart form data
- **AOT & trimming ready** — all net10.0 libraries are publish-AOT and trimming compatible
- **Zero runtime dependencies** — only `System.IO.Pipelines` and `System.Buffers` (via the netstandard2.0 abstractions layer)

## Quick Start

### TCP Echo Server

```csharp
using System.Buffers;
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
    public Task OnConnectedAsync(ITcpConnectionContext c, CancellationToken ct) => Task.CompletedTask;
    public Task OnClosedAsync(ITcpConnectionContext c, TcpCloseReason r, Exception? e, CancellationToken ct) => Task.CompletedTask;

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
using System.Text;
using PicoNode;
using PicoNode.Http;

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7003),
    ConnectionHandler = new HttpConnectionHandler(new HttpConnectionHandlerOptions
    {
        RequestHandler = new HttpRouter(new HttpRouterOptions
        {
            Routes =
            [
                HttpRoute.MapGet("/", static (_, _) =>
                    ValueTask.FromResult(new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
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

### Web Application (High-Level)

```csharp
using System.Net;
using PicoNode.Web;
using PicoWeb;

var app = new WebApp(new WebAppOptions { ServerHeader = "MyApp" });

// Middleware
app.Use(async (context, next, ct) =>
{
    var response = await next(context, ct);
    // add custom header to every response
    return response;
});

// Routes
app.MapGet("/", static (_, _) =>
    ValueTask.FromResult(WebResults.Text(200, "Hello, World!", "OK")));

app.MapGet("/users/{id}", static (ctx, _) =>
{
    var id = ctx.RouteValues["id"];
    return ValueTask.FromResult(WebResults.Json(200, $$"""{"id":"{{id}}"}""", "OK"));
});

app.MapPost("/echo", static (ctx, _) =>
{
    var body = System.Text.Encoding.UTF8.GetString(ctx.Request.Body.Span);
    return ValueTask.FromResult(WebResults.Text(200, body, "OK"));
});

await using var server = new WebServer(app, new WebServerOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
});
await server.StartAsync();
Console.ReadLine();
await server.StopAsync();
```

## Projects

| Project | Target | Description |
|---|---|---|
| **PicoNode.Abs** | netstandard2.0 | Core interfaces: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, fault codes, enums |
| **PicoNode** | net10.0 | `TcpNode` and `UdpNode` — production-grade async socket transports |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1 protocol layer with Span-based parsing |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, middleware, static files, compression, CORS, cookies, multipart |
| **PicoWeb** | net10.0 | `WebServer` — thin host that wires `WebApp` to `TcpNode` |

## Configuration

### TcpNode Options

| Option | Default | Description |
|---|---|---|
| `Endpoint` | *(required)* | Local endpoint to bind |
| `ConnectionHandler` | *(required)* | `ITcpConnectionHandler` implementation |
| `MaxConnections` | 1000 | Maximum concurrent connections |
| `IdleTimeout` | 2 min | Time before idle connections are closed |
| `DrainTimeout` | 5 sec | Grace period on shutdown for in-flight work |
| `SslOptions` | `null` | Set to enable TLS/SSL |
| `NoDelay` | `true` | TCP_NODELAY (Nagle disabled) |
| `Backlog` | 128 | Socket listen backlog |
| `FaultHandler` | `null` | Optional callback for non-fatal errors |

### UdpNode Options

| Option | Default | Description |
|---|---|---|
| `Endpoint` | *(required)* | Local endpoint to bind |
| `DatagramHandler` | *(required)* | `IUdpDatagramHandler` implementation |
| `DispatchWorkerCount` | 1 | Concurrent datagram processing workers |
| `DatagramQueueCapacity` | 1024 | Per-worker queue depth |
| `QueueOverflowMode` | `DropNewest` | Behavior when queues are full |
| `MulticastGroup` | `null` | Join a multicast group on start |
| `EnableBroadcast` | `true` | Allow sending broadcast datagrams |

### HttpConnectionHandler Options

| Option | Default | Description |
|---|---|---|
| `RequestHandler` | *(required)* | `HttpRequestHandler` delegate |
| `ServerHeader` | `null` | Value for the `Server` response header |
| `MaxRequestBytes` | 8192 | Maximum request size in bytes |

## Built-in Middleware

### Compression

```csharp
var compression = new CompressionMiddleware(CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Supports Brotli (`br`), Gzip, and Deflate. Automatically selects the best encoding from the client's `Accept-Encoding` header.

### Static Files

```csharp
var staticFiles = new StaticFileMiddleware("/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

Serves files from a root directory. Prevents directory traversal. Maps 30+ file extensions to MIME types.

### CORS

```csharp
app.Use((ctx, next, ct) =>
{
    var options = new CorsOptions
    {
        AllowedOrigins = ["https://example.com"],
        AllowedMethods = ["GET", "POST"],
        AllowCredentials = true,
    };
    var preflight = CorsHandler.HandlePreflight(ctx.Request, options);
    if (preflight is not null)
        return ValueTask.FromResult(preflight);
    // ... call next, then add CORS headers
});
```

### Cookies

```csharp
// Parse incoming cookies
var cookies = CookieParser.Parse(context.Request.HeaderFields);

// Build a Set-Cookie header
var setCookie = new SetCookieBuilder("session", "abc123")
    .Path("/")
    .HttpOnly()
    .Secure()
    .SameSite("Strict")
    .MaxAge(3600)
    .Build();
```

### Multipart Form Data

```csharp
var form = MultipartFormDataParser.Parse(context.Request);
if (form is not null)
{
    foreach (var field in form.Fields) { /* field.Name, field.Value */ }
    foreach (var file in form.Files)   { /* file.Name, file.FileName, file.ContentType, file.Data */ }
}
```

## Metrics

Both `TcpNode` and `UdpNode` expose real-time counters:

```csharp
// TCP
tcpNode.TotalAccepted      // connections accepted
tcpNode.ActiveConnections   // currently open
tcpNode.TotalBytesSent
tcpNode.TotalBytesReceived

// UDP
udpNode.TotalDatagramsReceived
udpNode.TotalDatagramsSent
udpNode.TotalDropped        // datagrams dropped due to overflow
```

## Samples

The [`samples/`](samples/) directory contains runnable examples:

| Sample | Port | Description |
|---|---|---|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | Raw TCP/UDP echo server |
| `PicoNode.Samples.Http` | 7003 | HTTP routing with `HttpRouter` |
| `PicoWeb.Samples` | 7004 | Full web app with middleware, route params, and echo |

Run any sample with:

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## Building & Testing

```bash
# Build the entire solution
dotnet build PicoNode.slnx -c Release

# Run all tests
dotnet test PicoNode.slnx -c Release

# Run a specific test project
dotnet test --project tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release
```

## Benchmarks

Microbenchmarks are provided via [PicoBench](https://github.com/PicoHex/PicoBench):

```bash
# Quick run
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick

# Full run
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- default

# Precise run
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- precise
```

Benchmarks cover HTTP parsing, router dispatch (hit / miss / 405), full pipeline, and localhost round-trips with baseline vs. routed handler comparisons.

## License

[MIT](LICENSE) © 2025 XiaoFei Du