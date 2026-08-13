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

### Web 应用（DI 优先 + 委托）

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

### Web 应用（基于控制器）

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

PicoNode.Web 在构造时需要 `ISvcContainer`（DI 优先）。作用域按请求自动创建。

### 在处理器中手动解析 DI

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

### 通过委托自动参数注入

处理器参数会自动解析（需要 `using PicoNode.Web;`）：
- `WebContext` → 当前上下文
- `CancellationToken` → 请求取消令牌
- 任何已注册的服务 → 从 DI 作用域解析

```csharp
app.MapGet("/users/{id}", async (WebContext ctx, IUserService svc) =>
{
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});
```

### AOT 兼容的序列化

PicoJetson 源生成器在编译期运行。处理器必须在用户代码中直接调用 `SerializeToUtf8Bytes<T>()` 才能触发生成器：

```csharp
// ✅ Triggers PicoJetson.Gen — UserDto serializer generated
var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);

// ❌ Does NOT trigger generator (cross-assembly generic)
Results.Json<UserDto>(200, user);
```

### WebApiBuilder（便捷方式）

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

### WebApiBuilder + 控制器（三阶段端点注册）

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

Controllers.Gen 和 PicoWeb.Gen 源生成器：
- 扫描 `Controllers/` 文件夹和 `app.MapGet/MapPost` 调用
- 为发现的 DTO 生成 `[PicoJsonSerializable]`
- 生成从 DI 解析控制器的端点存根

> **注意：** 基于控制器的模式需要 PicoJetson.Gen 来自动注册 DTO 序列化。
> 对于 MapXX 模式，请在处理器中显式调用 `PicoJetson.JsonSerializer.SerializeToUtf8Bytes<T>()`。
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
// UDP counters available via internal state
// (UdpNode tracks datagrams, bytes, and drops internally)
```

## 项目

| Project | Target | Description |
|---------|--------|-------------|
| **PicoNode.Abs** | netstandard2.0 | 核心接口：`INode`、`ITcpConnectionHandler`、`IUdpDatagramHandler`、故障码、枚举 |
| **PicoNode** | net10.0 | `TcpNode` 与 `UdpNode` — 生产级异步 socket 传输 |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`、`HttpRouter` — HTTP/1.1、HTTP/2、WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`、`WebRouter`、中间件、静态文件、压缩、CORS、DI |
| **PicoWeb** | net10.0 | `WebServer` — 将 `WebApp` 接入 `TcpNode` 的轻量宿主 |

## 示例

| Sample | Port | Description |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | 原始 TCP/UDP 回显服务器 |
| `PicoNode.Samples.Http` | 7003 | 使用 `HttpRouter` 的 HTTP 路由 |
| `PicoWeb.Samples` | 7004 | 带中间件和 DI 的完整 Web 应用 |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## 构建与测试

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

## 基准测试

微基准测试通过 [PicoBench](https://github.com/PicoHex/PicoBench) 提供：

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

基准覆盖 HTTP 解析、路由器分发（命中/未命中/405）、完整流水线以及 localhost 往返。

## 要求

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — 最大兼容性)
- PicoHex ecosystem (可选): PicoDI, PicoLog, PicoCfg

## 许可证

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — 面向 .NET 的分层网络
</p>
