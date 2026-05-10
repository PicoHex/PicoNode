# PicoNode

> 一個分層式的 AOT 原生 .NET 網路堆疊 — 從原始 TCP/UDP 通訊端到功能完整的 HTTP 網頁框架。

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

## 為什麼選擇 PicoNode？

| 特性 | PicoNode | ASP.NET Core |
|---------|----------|-------------|
| **相依模型** | 零必要的執行階段相依；隨選分層使用 | 需要 `Microsoft.AspNetCore.App` 架構參考 |
| **請求解析** | Span 基礎的串流處理，零複製 `System.IO.Pipelines` | 字串基礎搭配 `IO.Pipelines` 配接器 |
| **HTTP/2** | 內建 HPACK 解碼器，框架層級控制 | 透過 Kestrel 透明處理；低階存取有限 |
| **AOT 支援** | ✅ 原生支援 — 所有 net10.0 程式庫 | ⚠️ 需要修剪（trimming） |
| **DI / 紀錄 / 設定** | PicoDI + PicoLog + PicoCfg（PicoHex 原生） | Microsoft.Extensions.* |
| **WebSocket** | RFC 6455 框架編解碼器，訊息處理器抽象 | 透過中介軟體透明處理 |
| **程式碼行數** | 全堆疊約 15K 行 | ASP.NET Core 約 1M+ 行 |

> **設計優先順序：** PicoNode 優先考量配置效率和 AOT 相容性。熱路徑委派使用 `ValueTask`、ArrayPool 基礎的緩衝區管理，以及可選委派（無強迫配置）都是經過審慎權衡的取捨 — 它們讓傳輸層保持精簡且可預測。

### PicoHex 生態系

PicoNode 是 PicoHex 家族的一員，並與以下專案原生整合：

| 程式庫 | 用途 | NuGet |
|---------|---------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | 零反射的編譯時期 DI | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | 具結構化紀錄且具 AOT 安全性 | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | 原始碼產生的設定檔繫結 | `PicoCfg.Abs` |

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

## 快速入門

### 安裝

```bash
dotnet add package PicoNode
```

> 安裝 `PicoNode` 會一併引入 TCP/UDP 傳輸功能。如需更高層的抽象，請參考 `PicoNode.Http` 或 `PicoNode.Web`。

### 套件架構

PicoNode 以分層 NuGet 套件形式提供。選擇您需要的抽象層級：

| 套件 | 安裝時機… | 包含內容 |
|---------|--------------|-------------|
| **PicoWeb** | 想要一個可直接執行的網頁伺服器 | WebServer + WebApp + HTTP + TCP（全部傳遞相依） |
| **PicoNode.Web** | 想要網頁框架但不需要主機 | WebApp、路由、中介軟體、靜態檔案、DI |
| **PicoNode.Http** | 想要原始 HTTP 協定處理 | HTTP/1.1 + HTTP/2 + WebSocket、HttpRouter |
| **PicoNode** | 想要原始 TCP/UDP 傳輸 | TcpNode、UdpNode、通訊端生命週期、度量 |
| **PicoNode.Abs** | 正在撰寫處理器或擴充功能 | INode、ITcpConnectionHandler、核心合約 |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(host)      (web/DI)         (HTTP)            (transport)   (interfaces)
```

### TCP 回聲伺服器

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

### HTTP 伺服器（低階）

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

### 網頁應用程式（搭配 PicoHex 生態系）

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

## 設定

PicoNode 支援兩種設定模式：

### 程式碼優先（內嵌）

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### PicoCfg 繫結（AOT 安全，原始碼產生）

```csharp
var config = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var options = CfgBind.Bind<TcpNodeOptions>(config, "TcpNode");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // required
var node = new TcpNode(options);
```

### 執行時期重新載入

```csharp
// TcpNode 支援執行時期設定重新載入（Endpoint 除外）
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // 用於即時重新載入的 ICfgRoot
};
// 節點會啟動一個監聽設定變更的重新載入迴圈
```

### 主要選項

#### TcpNodeOptions

| 選項 | 預設值 | 說明 |
|--------|---------|-------------|
| `Endpoint` | *(必填)* | 要繫結的本機端點 |
| `ConnectionHandler` | *(必填)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | 最大同時連線數 |
| `IdleTimeout` | 2 分鐘 | 閒置連線關閉前的等待時間 |
| `DrainTimeout` | 5 秒 | 關機時的緩衝期間 |
| `SslOptions` | `null` | TLS/SSL 設定 |
| `NoDelay` | `true` | TCP_NODELAY（停用 Nagle 演算法） |
| `Logger` | `null` | 用於結構化診斷的 PicoLog `ILogger` |

#### UdpNodeOptions

| 選項 | 預設值 | 說明 |
|--------|---------|-------------|
| `Endpoint` | *(必填)* | 要繫結的本機端點 |
| `DatagramHandler` | *(必填)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | 並行資料包工作者數量 |
| `DatagramQueueCapacity` | 1024 | 每個工作者的佇列深度 |
| `QueueOverflowMode` | `DropNewest` | 佇列滿載時的行為 |
| `Logger` | `null` | PicoLog `ILogger` |

#### HttpConnectionHandlerOptions

| 選項 | 預設值 | 說明 |
|--------|---------|-------------|
| `RequestHandler` | *(必填)* | HttpRequestHandler 委派 |
| `ServerHeader` | `null` | `Server` 標頭的值 |
| `MaxRequestBytes` | 8192 | 最大請求大小（位元組） |
| `Logger` | `null` | PicoLog `ILogger` |

## 紀錄

PicoNode 使用 PicoLog 進行結構化診斷。所有非致命錯誤都會附帶操作上下文進行紀錄：

```csharp
var logger = new LoggerFactory([new ConsoleSink()])
    .CreateLogger("PicoNode.Tcp");

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
    ConnectionHandler = handler,
    Logger = logger, // 所有傳輸錯誤紀錄在此
});

// 紀錄輸出：
// [Error] Operation tcp.accept failed: AcceptFailed - System.Net.Sockets.SocketException
// [Warning] Operation tcp.reject.limit failed: SessionRejected
// [Debug] Socket shutdown during TLS teardown failed
```

**依錯誤碼的紀錄層級：**
- `Error`：StartFailed、StopFailed、AcceptFailed、ReceiveFailed、SendFailed、HandlerFailed、TlsFailed、DatagramReceiveFailed、DatagramHandlerFailed
- `Warning`：SessionRejected、DatagramDropped
- `Debug`：清理期間的通訊端關閉（盡力而為的操作）

## 相依性注入

PicoNode 的 Web 層與 PicoDI 整合，提供範圍請求處理：

```csharp
var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();
container.RegisterSingleton<ICache, RedisCache>();

var app = new WebApp();
app.Build(container); // 為每個請求注入範圍中介軟體

// 在路由處理程式中：
app.MapGet("/db", async (ctx, ct) =>
{
    var db = ctx.Services!.GetService<IDatabase>();
    var data = await db.QueryAsync("...");
    return WebResults.Json(200, data);
});
```

## 內建中介軟體

### 壓縮

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

支援 Brotli、Gzip 和 Deflate。會自動從用戶端的 `Accept-Encoding` 標頭中選擇最佳的編碼方式。

### 靜態檔案

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

從根目錄提供檔案服務。防止目錄遍歷攻擊。將 30 多種副檔名對應至 MIME 類型。

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

### Cookie 與多部分表單

```csharp
// Cookie 解析
var cookies = CookieParser.Parse(context.Request.HeaderFields);

// Set-Cookie
var setCookie = new SetCookieBuilder("session", "abc123")
    .Path("/").HttpOnly().Secure().SameSite("Strict").MaxAge(3600)
    .Build();

// 多部分表單資料
var form = MultipartFormDataParser.Parse(context.Request);
foreach (var field in form?.Fields ?? [])
    Console.WriteLine($"{field.Name} = {field.Value}");
foreach (var file in form?.Files ?? [])
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Data.Length} bytes)");
```

## 度量

`TcpNode` 和 `UdpNode` 都公開即時計數器：

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

## 專案

| 專案 | 目標框架 | 說明 |
|---------|--------|-------------|
| **PicoNode.Abs** | netstandard2.0 | 核心介面：`INode`、`ITcpConnectionHandler`、`IUdpDatagramHandler`、錯誤碼、列舉 |
| **PicoNode** | net10.0 | `TcpNode` 和 `UdpNode` — 生產等級的非同步通訊端傳輸 |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`、`HttpRouter` — HTTP/1.1、HTTP/2、WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`、`WebRouter`、中介軟體、靜態檔案、壓縮、CORS、DI |
| **PicoWeb** | net10.0 | `WebServer` — 將 `WebApp` 連接至 `TcpNode` 的精簡主機 |

## 範例

| 範例專案 | 埠號 | 說明 |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001（TCP）、7002（UDP） | 原始 TCP/UDP 回聲伺服器 |
| `PicoNode.Samples.Http` | 7003 | 使用 `HttpRouter` 的 HTTP 路由 |
| `PicoWeb.Samples` | 7004 | 包含中介軟體和 DI 的完整網頁應用程式 |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## 建置與測試

```bash
# 建置整個解決方案
dotnet build PicoNode.slnx -c Release

# 執行所有測試
dotnet test --solution PicoNode.slnx -c Release

# 執行特定測試專案
dotnet test --project tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release

# AOT 發行檢查
dotnet publish src/PicoWeb/PicoWeb.csproj -c Release -r win-x64 -p:PublishAot=true
```

## 基準測試

微基準測試透過 [PicoBench](https://github.com/PicoHex/PicoBench) 提供：

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

基準測試涵蓋 HTTP 解析、路由分派（命中/未命中/405）、完整管線，以及本機回環往返。

## 系統需求

- **.NET 10.0+**（PicoNode、PicoNode.Http、PicoNode.Web、PicoWeb）
- **.NET Standard 2.0**（PicoNode.Abs — 最大相容性）
- PicoHex 生態系（選用）：PicoDI、PicoLog、PicoCfg

## 授權

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — 適用於 .NET 的分層式網路堆疊
</p>
