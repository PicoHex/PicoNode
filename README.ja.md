# PicoNode

> 生のTCP/UDPソケットから本格的なHTTP Webフレームワークまでをカバーする、.NET向けのレイヤ化されたAOTネイティブなネットワーキングスタック

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

[English](README.md) | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | **日本語** | [Português (Brasil)](README.pt-BR.md) | [Русский](README.ru.md)

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

## PicoNodeの特長

| 機能 | PicoNode | ASP.NET Core |
|---------|----------|-------------|
| **依存モデル** | 必須ランタイム依存ゼロ。レイヤを選択して組み合わせ可能 | `Microsoft.AspNetCore.App` フレームワーク参照 |
| **リクエスト解析** | Spanベースのストリーミング、ゼロコピー `System.IO.Pipelines` | 文字列ベース + `IO.Pipelines` アダプタ |
| **HTTP/2** | インラインHPACKデコーダ、フレームレベル制御 | Kestrel経由で透過的。低レベルアクセスは限定 |
| **AOT対応** | ✅ ネイティブ — 全net10.0ライブラリで対応 | ⚠️ トリミングが必要 |
| **DI / ログ / 設定** | PicoDI + PicoLog + PicoCfg (PicoHexネイティブ) | Microsoft.Extensions.* |
| **WebSocket** | RFC 6455 フレームコーデック + メッセージハンドラ抽象化 | ミドルウェア経由で透過的 |
| **コード行数** | フルスタックで約15K行 | ASP.NET Coreは約100万行以上 |

> **設計方針:** PicoNodeはアロケーション効率とAOT互換性を優先します。ホットパスデリゲートでの`ValueTask`、ArrayPoolベースのバッファ管理、オプショナルデリゲート（強制アロケーションなし）は意図的なトレードオフであり、トランスポート層をコンパクトで予測可能な状態に保ちます。

### PicoHexエコシステム

PicoNodeはPicoHexファミリーの一員であり、以下のライブラリとネイティブ統合します:

| ライブラリ | 目的 | NuGet |
|---------|---------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | リフレクションゼロのコンパイル時DI | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | AOTセーフな構造化ロギング | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | ソース生成による設定バインディング | `PicoCfg.Abs` |

```
PicoNode.Abs        コアインターフェース                    (netstandard2.0, 依存ゼロ)
    ↓
PicoNode            TCP & UDP トランスポート + ILogger        (net10.0)
    ↓
PicoNode.Http       HTTP/1.1 + HTTP/2 + WebSocket             (net10.0)
    ↓
PicoNode.Web        Webフレームワーク + PicoDI ISvcContainer  (net10.0)
    ↓
PicoWeb             即時実行可能なWebサーバー + PicoCfg        (net10.0)
```

## クイックスタート

### インストール

```bash
dotnet add package PicoNode
```

> `PicoNode`をインストールするとTCP/UDPトランスポートが導入されます。上位レイヤを使用するには`PicoNode.Http`または`PicoNode.Web`を参照してください。

### パッケージアーキテクチャ

PicoNodeはレイヤ化されたNuGetパッケージとして提供されます。必要な抽象化レベルを正確に選択してください:

| パッケージ | インストールのタイミング | 含まれるもの |
|---------|--------------|-------------|
| **PicoWeb** | 即時実行可能なWebサーバーが必要な場合 | WebServer + WebApp + HTTP + TCP (すべて推移的に含む) |
| **PicoNode.Web** | ホスティングなしでWebフレームワークが必要な場合 | WebApp、ルーティング、ミドルウェア、静的ファイル、DI |
| **PicoNode.Http** | 生のHTTPプロトコル処理が必要な場合 | HTTP/1.1 + HTTP/2 + WebSocket、HttpRouter |
| **PicoNode** | 生のTCP/UDPトランスポートが必要な場合 | TcpNode、UdpNode、ソケットライフサイクル、メトリクス |
| **PicoNode.Abs** | ハンドラや拡張機能を書く場合 | INode、ITcpConnectionHandler、コアコントラクト |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode  →  PicoNode.Abs
(ホスト)     (Web/DI)         (HTTP)            (トランスポート)  (インターフェース)
```

### TCPエコーサーバー

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

### HTTPサーバー (低レベル)

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

### Webアプリケーション (PicoHexエコシステム利用)

```csharp
using System.Net;
using PicoDI.Abs;
using PicoLog.Abs;
using PicoCfg.Abs;
using PicoNode.Web;
using PicoWeb;

// 設定
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

// ミドルウェア
app.Use(async (context, next, ct) =>
{
    var response = await next(context, ct);
    return response;
});

// ルート
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

// DI対応ホスティング
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

PicoNodeは2つの設定モードをサポートします:

### コードファースト (インライン)

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### PicoCfgバインディング (AOTセーフ、ソース生成)

```csharp
var config = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("PICONODE_")
    .BuildAsync();

var options = CfgBind.Bind<TcpNodeOptions>(config, "TcpNode");
options.Endpoint = new IPEndPoint(IPAddress.Any, 8080); // 必須
var node = new TcpNode(options);
```

### 実行時リロード

```csharp
// TcpNodeは実行時の設定リロードをサポートします (Endpointを除く)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // ライブリロード用の ICfgRoot
};
// ノードが設定変更を監視するリロードループを開始
```

### 主要オプション

#### TcpNodeOptions

| オプション | デフォルト | 説明 |
|--------|---------|-------------|
| `Endpoint` | *(必須)* | バインドするローカルエンドポイント |
| `ConnectionHandler` | *(必須)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | 最大同時接続数 |
| `IdleTimeout` | 2分 | アイドル接続が閉じられるまでの時間 |
| `DrainTimeout` | 5秒 | シャットダウン時の猶予期間 |
| `SslOptions` | `null` | TLS/SSL設定 |
| `NoDelay` | `true` | TCP_NODELAY (Nagle無効) |
| `Logger` | `null` | 構造化診断用の PicoLog `ILogger` |

#### UdpNodeOptions

| オプション | デフォルト | 説明 |
|--------|---------|-------------|
| `Endpoint` | *(必須)* | バインドするローカルエンドポイント |
| `DatagramHandler` | *(必須)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | データグラムワーカーの並列数 |
| `DatagramQueueCapacity` | 1024 | ワーカーあたりのキュー深度 |
| `QueueOverflowMode` | `DropNewest` | キュー満杯時の動作 |
| `Logger` | `null` | PicoLog `ILogger` |

#### HttpConnectionHandlerOptions

| オプション | デフォルト | 説明 |
|--------|---------|-------------|
| `RequestHandler` | *(必須)* | HttpRequestHandler デリゲート |
| `ServerHeader` | `null` | `Server` ヘッダーの値 |
| `MaxRequestBytes` | 8192 | リクエストの最大サイズ（バイト） |
| `Logger` | `null` | PicoLog `ILogger` |

## ロギング

PicoNodeは構造化診断にPicoLogを使用します。致命的でないエラーはすべて操作コンテキストとともに記録されます:

```csharp
var logger = new LoggerFactory([new ConsoleSink()])
    .CreateLogger("PicoNode.Tcp");

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 7001),
    ConnectionHandler = handler,
    Logger = logger, // すべてのトランスポート障害がここに記録される
});

// ログ出力:
// [Error] Operation tcp.accept failed: AcceptFailed - System.Net.Sockets.SocketException
// [Warning] Operation tcp.reject.limit failed: SessionRejected
// [Debug] Socket shutdown during TLS teardown failed
```

**障害コード別のログレベル:**
- `Error`: StartFailed、StopFailed、AcceptFailed、ReceiveFailed、SendFailed、HandlerFailed、TlsFailed、DatagramReceiveFailed、DatagramHandlerFailed
- `Warning`: SessionRejected、DatagramDropped
- `Debug`: クリーンアップ中のソケットシャットダウン (ベストエフォート操作)

## 依存性注入

PicoNodeのWebレイヤはPicoDIと統合し、スコープ付きリクエスト処理を提供します:

```csharp
var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();
container.RegisterSingleton<ICache, RedisCache>();

var app = new WebApp();
app.Build(container); // リクエストごとにスコープミドルウェアを注入

// ルートハンドラ内で:
app.MapGet("/db", async (ctx, ct) =>
{
    var db = ctx.Services!.GetService<IDatabase>();
    var data = await db.QueryAsync("...");
    return WebResults.Json(200, data);
});
```

## 組み込みミドルウェア

### 圧縮

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Brotli、Gzip、Deflateをサポート。クライアントの `Accept-Encoding` ヘッダーから最適なエンコーディングを自動選択します。

### 静的ファイル

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

ルートディレクトリからファイルを配信します。ディレクトリトラバーサルを防止します。30以上のファイル拡張子をMIMEタイプにマッピングします。

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

### Cookieとマルチパート

```csharp
// Cookieの解析
var cookies = CookieParser.Parse(context.Request.HeaderFields);

// Set-Cookie
var setCookie = new SetCookieBuilder("session", "abc123")
    .Path("/").HttpOnly().Secure().SameSite("Strict").MaxAge(3600)
    .Build();

// マルチパートフォームデータ
var form = MultipartFormDataParser.Parse(context.Request);
foreach (var field in form?.Fields ?? [])
    Console.WriteLine($"{field.Name} = {field.Value}");
foreach (var file in form?.Files ?? [])
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Data.Length} bytes)");
```

## メトリクス

`TcpNode` と `UdpNode` はどちらもリアルタイムカウンターを公開します:

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

## プロジェクト

| プロジェクト | ターゲット | 説明 |
|---------|--------|-------------|
| **PicoNode.Abs** | netstandard2.0 | コアインターフェース: `INode`、`ITcpConnectionHandler`、`IUdpDatagramHandler`、障害コード、enum |
| **PicoNode** | net10.0 | `TcpNode` と `UdpNode` — プロダクション品質の非同期ソケットトランスポート |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`、`HttpRouter` — HTTP/1.1、HTTP/2、WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`、`WebRouter`、ミドルウェア、静的ファイル、圧縮、CORS、DI |
| **PicoWeb** | net10.0 | `WebServer` — `WebApp` を `TcpNode` に接続する薄いホスティング層 |

## サンプル

| サンプル | ポート | 説明 |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | 生のTCP/UDPエコーサーバー |
| `PicoNode.Samples.Http` | 7003 | `HttpRouter` によるHTTPルーティング |
| `PicoWeb.Samples` | 7004 | ミドルウェアとDIを備えた本格的なWebアプリ |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## ビルドとテスト

```bash
# ソリューション全体のビルド
dotnet build PicoNode.slnx -c Release

# 全テストの実行
dotnet test --solution PicoNode.slnx -c Release

# 特定のテストプロジェクトの実行
dotnet test --project tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release

# AOT公開の確認
dotnet publish src/PicoWeb/PicoWeb.csproj -c Release -r win-x64 -p:PublishAot=true
```

## ベンチマーク

マイクロベンチマークは [PicoBench](https://github.com/PicoHex/PicoBench) 経由で提供されます:

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

ベンチマークはHTTPパース、ルーターディスパッチ (ヒット/ミス/405)、フルパイプライン、およびlocalhostラウンドトリップをカバーします。

## 要件

- **.NET 10.0以上** (PicoNode、PicoNode.Http、PicoNode.Web、PicoWeb)
- **.NET Standard 2.0** (PicoNode.Abs — 最大互換性)
- PicoHexエコシステム (オプション): PicoDI、PicoLog、PicoCfg

## ライセンス

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — layered networking for .NET
</p>
