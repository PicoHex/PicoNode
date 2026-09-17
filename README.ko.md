# PicoNode

> .NET을 위한 계층형 AOT 네이티브 네트워킹 스택 — 원시 TCP/UDP 소켓부터 완전한 HTTP 웹 프레임워크까지.

[![NuGet](https://img.shields.io/nuget/v/PicoNode.svg)](https://www.nuget.org/packages/PicoNode)
[![License](https://img.shields.io/github/license/PicoHex/PicoNode)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com)

[English](README.md) | [简体中文](README.zh.md) | [繁體中文](README.zh-TW.md) | [Deutsch](README.de.md) | [Español](README.es.md) | [Français](README.fr.md) | [日本語](README.ja.md) | **한국어** | [Português (Brasil)](README.pt-BR.md) | [Русский](README.ru.md)

```
┌─────────────────────────────────────────────────────────────┐
│  PicoNode: .NET을 위한 계층형 네트워킹                      │
│  ✓ 비동기 I/O 기반 원시 TCP/UDP 소켓 전송                   │
│  ✓ HTTP/1.1 + HTTP/2 + WebSocket 프로토콜                   │
│  ✓ 미들웨어·라우팅·정적 파일을 갖춘 웹 프레임워크           │
│  ✓ PicoHex 생태계 통합 (PicoDI/PicoLog/PicoCfg)             │
│  ✓ 모든 net10.0 계층에서 네이티브 AOT 호환                  │
│  ✓ 최소한의 런타임 의존성                                   │
└─────────────────────────────────────────────────────────────┘
```

## PicoNode를 선택하는 이유

| 기능 | PicoNode | ASP.NET Core |
|---------|----------|-------------|
| **의존성 모델** | 필수 런타임 의존성 없음; 계층 선택 가능 | `Microsoft.AspNetCore.App` 프레임워크 참조 |
| **요청 파싱** | Span 기반 스트리밍, 제로 카피 `System.IO.Pipelines` | 문자열 기반 + `IO.Pipelines` 어댑터 |
| **HTTP/2** | 인라인 HPACK 디코더, 프레임 수준 제어 | Kestrel을 통한 투명 처리; 저수준 접근 제한 |
| **AOT 지원** | ✅ 네이티브 — 모든 net10.0 라이브러리 | ⚠️ 트리밍 필요 |
| **DI / 로깅 / 구성** | PicoDI + PicoLog + PicoCfg (PicoHex 네이티브) | Microsoft.Extensions.* |
| **WebSocket** | 메시지 핸들러 추상화를 갖춘 RFC 6455 프레임 코덱 | 미들웨어를 통한 투명 처리 |
| **코드 라인 수** | 전체 스택 약 17K | ASP.NET Core 약 1M+ |

> **설계 우선순위:** PicoNode는 할당 효율성과 AOT 호환성을 우선합니다. 핫 패스 델리게이트의 `ValueTask`, ArrayPool 기반 버퍼 관리, 선택적 델리게이트(강제 할당 없음)는 의도적인 트레이드오프로, 전송 계층을 작고 예측 가능하게 유지합니다.

### PicoHex 생태계

PicoNode는 PicoHex 제품군의 일부이며 다음 라이브러리와 네이티브로 통합됩니다:

| 라이브러리 | 용도 | NuGet |
|---------|---------|-------|
| [PicoDI](https://github.com/PicoHex/PicoDI) | 제로 리플렉션 컴파일 타임 DI | `PicoDI.Abs` |
| [PicoLog](https://github.com/PicoHex/PicoLog) | AOT 안전 구조적 로깅 | `PicoLog.Abs` |
| [PicoCfg](https://github.com/PicoHex/PicoCfg) | 소스 생성 구성 바인딩 | `PicoCfg.Abs` |

```
PicoNode.Abs        코어 인터페이스                          (net10.0, 의존성 없음)
    ↓
PicoNode            TCP & UDP 전송 계층 + ILogger            (net10.0)
    ↓
PicoNode.Http       HTTP/1.1 + HTTP/2 + WebSocket            (net10.0)
    ↓
PicoNode.Web        Web 프레임워크 + PicoDI ISvcContainer    (net10.0)
    ↓
PicoWeb             즉시 실행 가능한 웹 서버 + PicoCfg       (net10.0)
PicoJsonRpc         JSON-RPC 2.0 over stdio (NDJSON)         (net10.0)
```

## 빠른 시작

### 설치

```bash
dotnet add package PicoNode
```

> `PicoNode`를 설치하면 TCP/UDP 전송 계층이 포함됩니다. 상위 계층이 필요하면 `PicoNode.Http` 또는 `PicoNode.Web`을 참조하세요.

### 패키지 아키텍처

PicoNode는 계층형 NuGet 패키지로 제공됩니다. 필요한 추상화 수준을 정확히 선택하세요:

| 패키지 | 설치 시점 | 제공 내용 |
|---------|--------------|-------------|
| **PicoWeb** | 바로 실행 가능한 웹 서버가 필요할 때 | WebServer + WebApp + HTTP + TCP (모두 전이 포함) |
| **PicoNode.Web** | 호스팅 없이 웹 프레임워크만 필요할 때 | WebApp, 라우팅, 미들웨어, 정적 파일, DI |
| **PicoNode.Http** | 원시 HTTP 프로토콜 처리가 필요할 때 | HTTP/1.1 + HTTP/2 + WebSocket, HttpRouter |
| **PicoNode** | 원시 TCP/UDP 전송이 필요할 때 | TcpNode, UdpNode, 소켓 수명 주기, 메트릭 |
| **PicoNode.Abs** | 핸들러나 확장을 작성할 때 | INode, ITcpConnectionHandler, 코어 계약 |

```
PicoWeb  →  PicoNode.Web  →  PicoNode.Http  →  PicoNode.Abs
PicoWeb  →  PicoNode  →  PicoNode.Abs
```

### TCP 에코 서버

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

### HTTP 서버 (저수준)

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
                Route<HttpRequestHandler>.MapGet("/", static (_, _) =>
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

### 웹 애플리케이션 (DI 우선 + 델리게이트)

```csharp
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .ConfigureApp(_ => new WebAppOptions { ServerHeader = "MyApp" })
    // ConfigureApp receives the CURRENT options — later calls can build on
    // earlier configuration instead of starting from defaults.
    .RegisterScoped<IUserService, UserService>()
    .Build();

api.App.MapGet("/", static (WebContext ctx, CancellationToken _) =>
    ValueTask.FromResult(Results.Text(200, "Hello, World!")));

api.App.MapGet("/users/{id}", async (WebContext ctx, CancellationToken _) =>
{
    var svc = (IUserService)ctx.Services.GetService(typeof(IUserService))!;
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});

api.App.MapPost("/echo", async (WebContext ctx, CancellationToken _) =>
{
    using var reader = new StreamReader(ctx.Request.BodyStream);
    var body = await reader.ReadToEndAsync();
    return Results.Text(200, body);
});

await api.RunAsync("http://+:8080");
```

### 웹 애플리케이션 (컨트롤러 기반)

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

// Controllers.Gen auto-generates endpoint stubs (DTO serializers: PicoJetson.Gen)
await api.RunAsync("http://+:8080");
```

## 구성

PicoNode는 두 가지 구성 모드를 지원합니다:

### 코드 우선 (인라인)

```csharp
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 500,
    IdleTimeout = TimeSpan.FromMinutes(5),
};
var node = new TcpNode(options);
```

### PicoCfg 바인딩 (AOT 안전, 소스 생성)

```csharp
var config = await Cfg.CreateBuilder()
    .Add(new Dictionary<string, string>
    {
        ["App:Name"] = "PicoCfg",
        ["App:Enabled"] = "true",
    })
    .BuildAsync();

var settings = CfgBind.Bind<AppSettings>(config, "App");

public sealed class AppSettings
{
    public string? Name { get; set; }
    public bool Enabled { get; set; }
}
```

### 런타임 리로드

```csharp
// TcpNode supports runtime config reload (except Endpoint)
var options = new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 8080),
    Config = config, // ICfgRoot for live reload
};
// Node starts a reload loop watching for config changes
```

### 주요 옵션

#### TcpNodeOptions

| 옵션 | 기본값 | 설명 |
|--------|---------|-------------|
| `Endpoint` | *(필수)* | 바인딩할 로컬 엔드포인트 |
| `ConnectionHandler` | *(필수)* | `ITcpConnectionHandler` |
| `MaxConnections` | 1000 | 최대 동시 연결 수 |
| `IdleTimeout` | 2분 | 유휴 연결을 닫기까지의 시간 |
| `DrainTimeout` | 5초 | 종료 시 유예 기간 |
| `SslOptions` | `null` | TLS/SSL 구성 |
| `NoDelay` | `true` | TCP_NODELAY (Nagle 비활성화) |
| `Logger` | `null` | 구조적 진단용 PicoLog `ILogger` |

#### UdpNodeOptions

| 옵션 | 기본값 | 설명 |
|--------|---------|-------------|
| `Endpoint` | *(필수)* | 바인딩할 로컬 엔드포인트 |
| `DatagramHandler` | *(필수)* | `IUdpDatagramHandler` |
| `DispatchWorkerCount` | 1 | 동시 데이터그램 워커 수 |
| `DatagramQueueCapacity` | 1024 | 워커별 큐 깊이 |
| `QueueOverflowMode` | `DropNewest` | 큐가 가득 찼을 때의 동작 |
| `Logger` | `null` | PicoLog `ILogger` |

#### HttpConnectionHandlerOptions

| 옵션 | 기본값 | 설명 |
|--------|---------|-------------|
| `RequestHandler` | *(필수)* | HttpRequestHandler 델리게이트 |
| `ServerHeader` | `null` | `Server` 헤더 값 |
| `MaxRequestBytes` | 8192 | 최대 요청 크기(바이트) |
| `MaxRequestBodySize` | 67108864 (64 MB) | 최대 요청 본문 크기(바이트) |
| `StreamingResponseBufferSize` | 4096 | 스트리밍 응답 본문 버퍼 크기 |
| `RequestTimeout` | 30초 | 완전한 요청을 수신할 최대 시간 |
| `WebSocketMessageHandler` | `null` | WebSocket 메시지 핸들러 |
| `WebSocketMaxMessageSize` | 262144 (256 KB) | 재조립된 WebSocket 메시지의 최대 크기 |
| `Logger` | `null` | PicoLog `ILogger` |

## 로깅

PicoNode는 구조적 진단을 위해 PicoLog를 사용합니다. 모든 치명적이지 않은 오류는 작업 컨텍스트와 함께 기록됩니다:

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

**폴트 코드별 로그 수준:**
- `Error`: StartFailed, StopFailed, AcceptFailed, ReceiveFailed, SendFailed, HandlerFailed, TlsFailed, DatagramReceiveFailed, DatagramHandlerFailed
- `Warning`: SessionRejected, DatagramDropped
- `Debug`: Socket shutdown during cleanup (best-effort operations)

## 의존성 주입

PicoNode.Web은 생성 시점에 `ISvcContainer`를 요구합니다(DI First). 스코프는 요청마다 자동으로 생성됩니다.

### 핸들러에서 수동 DI 해석

```csharp
using PicoNode.Web;
using PicoWeb;
using PicoJetson;

var container = new SvcContainer();
container.RegisterScoped<IDatabase, SqlDatabase>();

var app = new WebApp(container);
app.MapGet("/db", async (WebContext ctx, CancellationToken _) =>
{
    var db = (IDatabase)ctx.Services.GetService(typeof(IDatabase))!;
    var data = await db!.QueryAsync("...");
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(data);
    return Results.Json(200, bytes);
});

app.Build();
```

### 핸들러 내부에서 서비스 해석

핸들러는 항상 `WebRequestHandler` 시그니처 `(WebContext, CancellationToken)`를 사용합니다.
서비스는 `ctx.Services`를 통해 요청 스코프에서 가져오며, 매개변수 주입은 없습니다:

```csharp
app.MapGet("/users/{id}", async (WebContext ctx, CancellationToken _) =>
{
    var svc = (IUserService)ctx.Services.GetService(typeof(IUserService))!;
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});
```

### AOT 호환 직렬화

PicoJetson 소스 생성기는 컴파일 타임에 실행됩니다. 생성기를 트리거하려면 핸들러가 사용자 코드에서 직접 `SerializeToUtf8Bytes<T>()`를 호출해야 합니다:

```csharp
// ✅ Triggers PicoJetson.Gen — UserDto serializer generated
var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);

// ❌ Does NOT trigger generator (cross-assembly generic)
Results.Json<UserDto>(200, user);
```

### WebApiBuilder (편의)

```csharp
using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .RegisterScoped<IUserService, UserService>()
    .ConfigureJson(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    .Build();

api.App.MapGet("/api/users/{id}", async (WebContext ctx, CancellationToken _) =>
{
    var svc = (IUserService)ctx.Services.GetService(typeof(IUserService))!;
    var user = await svc.GetByIdAsync(ctx.RouteValues["id"]);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});

await api.RunAsync("http://+:5000");
```

### WebApiBuilder와 컨트롤러 (엔드포인트 등록)

```csharp
// 1. Controller in Controllers/ folder (convention)
//    Controllers/UsersController.cs
public class UsersController
{
    public UserDto GetUser(int id) { return new UserDto { ... }; }
    public List<UserDto> GetAllUsers() { return ...; }
}

// 2. Controllers.Gen auto-registers controllers in DI via a generated
//    [ModuleInitializer] (no manual registration needed).
// 3. Call EndpointRegistrar (auto-generated by Controllers.Gen) to wire the routes:
EndpointRegistrar.RegisterAll(app);

// 4. Then run the app:
new WebApiBuilder()
    .RegisterScoped<UsersController>()
    .Build()
    .RunAsync("http://+:5000");
```

Controllers.Gen 소스 생성기:
- `Controllers/` 폴더(또는 `[ApiController]` 클래스)를 스캔합니다
- DI에서 컨트롤러를 해석해 등록하는 엔드포인트 스텁을 생성합니다

PicoWeb.Gen은 `app.MapGet/MapPost` 핸들러의 반환 형식에 대해 빌드 시 진단(PWR001)을 출력하며
소스 코드를 생성하지 않습니다. DTO 직렬화기는 명시적인 `SerializeToUtf8Bytes<T>()`
호출 지점에서 PicoJetson.Gen이 생성합니다.

> **참고:** 자체 컨트롤러가 없으면서 이미 `EndpointRegistrar`를 내보내는 애플리케이션(예: 샘플 앱을 참조하는 통합 테스트)을 참조하는 프로젝트에서는 Controllers.Gen이 자체 등록기를 생성하지 않고, `RegisterAll`은 참조된 앱의 등록기에 바인딩됩니다 — CS0436 중복 형식 오류와 조용한 빈 등록을 방지합니다.

> **참고:** 컨트롤러 기반 패턴은 자동 DTO 직렬화 등록을 위해 PicoJetson.Gen이 필요합니다.
> MapXX 패턴에서는 핸들러에서 `PicoJetson.JsonSerializer.SerializeToUtf8Bytes<T>()`를 직접 호출하세요.

## 내장 미들웨어

### 압축

```csharp
var compression = new CompressionMiddleware(
    CompressionLevel.Fastest, minimumBodySize: 860);
app.Use(compression.InvokeAsync);
```

Brotli, Gzip, Deflate를 지원합니다. 클라이언트의 `Accept-Encoding` 헤더에서 최적 인코딩을 자동 선택합니다.

### 정적 파일

```csharp
var staticFiles = new StaticFileMiddleware(
    "/path/to/wwwroot", requestPathPrefix: "/static");
app.Use(staticFiles.InvokeAsync);
```

루트 디렉터리에서 파일을 제공합니다. 디렉터리 트래버설을 차단합니다. 30개 이상의 파일 확장자를 MIME 타입에 매핑합니다.

### CORS

```csharp
app.Use(async (ctx, next, ct) =>
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

### 쿠키와 멀티파트

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
    Console.WriteLine($"{file.FileName}: {file.ContentType} ({file.Content.Length} bytes)");
```

## 메트릭

`TcpNode`와 `UdpNode` 모두 실시간 카운터를 노출합니다:

```csharp
// TCP
var tcpMetrics = node.GetMetrics();  // only TcpNode
Console.WriteLine($"Accepted: {tcpMetrics.TotalAccepted}");
Console.WriteLine($"Active: {tcpMetrics.ActiveConnections}");
Console.WriteLine($"Sent: {tcpMetrics.TotalBytesSent}");
Console.WriteLine($"Received: {tcpMetrics.TotalBytesReceived}");

// UDP counters available via internal state
// (UdpNode tracks datagrams, bytes, and drops internally)
```

## 프로젝트

| 프로젝트 | 대상 | 설명 |
|---------|--------|-------------|
| **PicoNode.Abs** | net10.0 | 코어 인터페이스: `INode`, `ITcpConnectionHandler`, `IUdpDatagramHandler`, 폴트 코드, 열거형 |
| **PicoNode** | net10.0 | `TcpNode`와 `UdpNode` — 프로덕션급 비동기 소켓 전송 |
| **PicoNode.Http** | net10.0 | `HttpConnectionHandler`, `HttpRouter` — HTTP/1.1, HTTP/2, WebSocket |
| **PicoNode.Web** | net10.0 | `WebApp`, `WebRouter`, 미들웨어, 정적 파일, 압축, CORS, DI |
| **PicoWeb** | net10.0 | `WebServer` — `WebApp`을 `TcpNode`에 연결하는 얇은 호스트 |

## 샘플

| 샘플 | 포트 | 설명 |
|--------|------|-------------|
| `PicoNode.Samples.Echo` | 7001 (TCP), 7002 (UDP) | 원시 TCP/UDP 에코 서버 |
| `PicoNode.Samples.Http` | 7003 | `HttpRouter`를 사용한 HTTP 라우팅 |
| `PicoWeb.Samples` | 7004 | 미들웨어와 DI를 갖춘 완전한 웹 앱 |

```bash
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

## 빌드와 테스트

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

## 벤치마크

마이크로벤치마크는 [PicoBench](https://github.com/PicoHex/PicoBench)를 통해 제공됩니다:

```bash
dotnet run --project benchmarks/PicoNode.Http.Benchmarks/PicoNode.Http.Benchmarks.csproj -c Release -- quick
```

벤치마크는 HTTP 파싱, 라우터 디스패치(hit/miss/405), 전체 파이프라인, localhost 왕복을 다룹니다.

## 요구 사항

- **.NET 10.0+** (PicoNode, PicoNode.Http, PicoNode.Web, PicoWeb)
- **.NET 10.0** (PicoNode.Abs — 최대 호환성)
- PicoHex 생태계(선택): PicoDI, PicoLog, PicoCfg

## 라이선스

[MIT](LICENSE) © 2025 XiaoFei Du

---

<p align="center">
  <b>PicoNode</b> — layered networking for .NET
</p>
