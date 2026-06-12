# PicoNode

> 分层式、原生 AOT 的 .NET 网络栈 — 从裸 TCP/UDP 套接字到功能完备的 HTTP Web 框架。

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

**English** | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [日本語](README.ja.md) | [Português (Brasil)](README.pt-BR.md) | [Русский](README.ru.md)

```
┌─────────────────────────────────────────────────────────────┐
│  PicoNode：面向 .NET 的分层式网络框架                           │
│  ✓ 基于异步 I/O 的裸 TCP/UDP 套接字传输层                      │
│  ✓ 支持 HTTP/1.1 + HTTP/2 + WebSocket 协议                    │
│  ✓ 包含中间件、路由、静态文件的 Web 框架                        │
│  ✓ 集成 PicoHex 生态（PicoDI / PicoLog / PicoCfg）             │
│  ✓ 所有 net10.0 层均兼容原生 AOT                               │
│  ✓ 最小化运行时依赖                                           │
└─────────────────────────────────────────────────────────────┘
```

## 为什么选择 PicoNode？

| 特性 | PicoNode | ASP.NET Core |
|---------|----------|-------------|
| **依赖模型** | 零必需运行时依赖；分层按需选用 | `Microsoft.AspNetCore.App` 框架引用 |
| **请求解析** | 基于 Span 的流式解析，`System.IO.Pipelines` 零拷贝 | 基于字符串，`IO.Pipelines` 适配器 |
| **HTTP/2** | 内联 HPACK 解码器，帧级控制 | 通过 Kestrel 透明处理；底层访问能力有限 |
| **AOT 支持** | ✅ 原生支持 — 所有 net10.0 库 | ⚠️ 需要裁剪 |
| **DI / 日志 / 配置** | PicoDI + PicoLog + PicoCfg（PicoHex 原生） | Microsoft.Extensions.* |
| **WebSocket** | RFC 6455 帧编码器加消息处理器抽象 | 通过中间件透明处理 |
| **代码行数** | 全栈约 15K 行 | ASP.NET Core 约 100 万+ 行 |

> **设计原则：** PicoNode 优先考虑分配效率和 AOT 兼容性。热路径委托使用 `ValueTask`，基于 ArrayPool 的缓冲区管理，可选委托（无强制分配）— 这些都是有意为之的权衡，旨在保持传输层紧凑且可预测。

### PicoHex 生态

PicoNode 是 PicoHex 家族的一员，原生集成以下库：

| 库 | 用途 | NuGet |
|---------|---------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | 零反射编译时 DI | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | AOT 安全的结构化日志 | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | 源代码生成的配置绑定 | `PicoCfg.Abs` |

```
PicoNode.Abs        核心接口                                  (netstandard2.0, zero deps)
    ↓
PicoNode             TCP & UDP 传输层 + ILogger               (net10.0)
    ↓
PicoNode.Http        HTTP/1.1 + HTTP/2 + WebSocket            (net10.0)
    ↓
PicoNode.Web         Web 框架 + PicoDI ISvcContainer          (net10.0)
    ↓
PicoWeb              开箱即用的 Web 服务器 + PicoCfg            (net10.0)
```

## 快速开始

### 安装

```bash
dotnet add package PicoNode
```

> 安装 `PicoNode` 将引入 TCP/UDP 传输层。需要更上层功能时，请引用 `PicoNode.Http` 或 `PicoNode.Web`。

### 包结构

PicoNode 以分层 NuGet 包的形式发布。按需选择你需要的抽象层级：

| 包 | 何时安装 | 包含内容 |
|---------|--------------|-------------|
| **PicoWeb** | 你想要一个开箱即用的 Web 服务器 | WebServer + WebApp + HTTP + TCP（全部传递依赖） |
| **PicoNode.Web** | 你想要 Web 框架但不需托管层 | WebApp、路由、中间件、静态文件、DI |
| **PicoNode.Http** | 你想要原生 HTTP 协议处理 | HTTP/1.1 + HTTP/2 + WebSocket、HttpRouter |
| **PicoNode** | 你想要裸 TCP/UDP 传输层 | TcpNode、UdpNode、套接字生命周期、指标 |
| **PicoNode.Abs** | 你在编写处理器或扩展 | INode、ITcpConnectionHandler、核心契约 |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(宿主)      (Web / DI)       (HTTP)            (传输层)      (接口)
```

### TCP 回显服务器

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

### HTTP 服务器（底层）

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

### Web 应用（搭配 PicoHex 生态）

```csharp
using System.Net;
using PicoDI.Abs;
using PicoLog.Abs;
using PicoNode.Web;
using PicoWeb;


var app = new WebApp(new WebAppOptions
{
    ServerHeader = "MyApp",
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

## 配置

PicoNode 支持两种配置模式：

### 代码优先（内联）

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### PicoCfg 绑定（AOT 安全，源代码生成）

```csharp
var config = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var settings = CfgBind.Bind<AppSettings>(config, "App");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // required
var node = new TcpNode(options);
```

### 运行时重载

```csharp
// TcpNode supports runtime config reload (except Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
};
// Node starts a reload loop watching for config changes
```

### 关键选项

#### TcpNodeOptions

| 选项 | 默认值 | 说明 |
|--------|---------|-------------|
| `Endpoint` | *（必填）* | 要绑定的本地端点 |
| `ConnectionHandler` | *（必填）* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | 最大并发连接数 |
| `IdleTimeout` | 2 分钟 | 空闲连接关闭前的等待时间 |
| `DrainTimeout` | 5 秒 | 关闭时的宽限期 |
| `SslOptions` | `null` | TLS/SSL 配置 |
| `NoDelay` | `true` | TCP_NODELAY（禁用 Nagle 算法） |
| `Logger` | `null` | 用于结构化诊断的 PicoLog `ILogger` |

#### UdpNodeOptions

| 选项 | 默认值 | 说明 |
|--------|---------|-------------|
| `Endpoint` | *（必填）* | 要绑定的本地端点 |
| `DatagramHandler` | *（必填）* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | 并发数据报工作线程数 |
| `DatagramQueueCapacity` | 1024 | 每个工作线程的队列深度 |
| `QueueOverflowMode` | `DropNewest` | 队列满时的处理策略 |
| `Logger` | `null` | PicoLog `ILogger` |

#### HttpConnectionHandlerOptions

| 选项 | 默认值 | 说明 |
|--------|---------|-------------|
| `RequestHandler` | *（必填）* | HttpRequestHandler 委托 |
| `ServerHeader` | `null` | `Server` 响应头的值 |
| `MaxRequestBytes` | 8192 | 最大请求大小（字节） |
| `Logger` | `null` | PicoLog `ILogger` |

## 日志

PicoNode 使用 PicoLog 进行结构化诊断。所有非致命错误都会附带操作上下文进行记录：

```csharp

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

**按错误码划分的日志级别：**
- `Error`：StartFailed、StopFailed、AcceptFailed、ReceiveFailed、SendFailed、HandlerFailed、TlsFailed、DatagramReceiveFailed、DatagramHandlerFailed
- `Warning`：SessionRejected、DatagramDropped
- `Debug`：清理期间套接字关闭（尽力而为的操作）

## 依赖注入

PicoNode 的 Web 层与 PicoDI 集成，支持作用域请求处理：

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

## 内置中间件

### 压缩

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

支持 Brotli、Gzip 和 Deflate。根据客户端 `Accept-Encoding` 请求头自动选择最佳编码。

### 静态文件

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

从根目录提供文件服务。防止目录遍历。将 30 多种文件扩展名映射到 MIME 类型。

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

### Cookies 与 Multipart

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
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Content.Length bytes)");
```

## 指标

`TcpNode` 和 `UdpNode` 都暴露实时计数器：

```csharp
// TCP
var tcpMetrics = tcpNode.GetMetrics();
Console.WriteLine($"Accepted: {tcpMetrics.TotalAccepted}");
Console.WriteLine($"Active: {tcpMetrics.ActiveConnections}");
Console.WriteLine($"Sent: {tcpMetrics.TotalBytesSent}");
Console.WriteLine($"Received: {tcpMetrics.TotalBytesReceived}");
n// UDP counters available via internal state
// (UdpNode tracks datagrams, bytes, and drops internally)

