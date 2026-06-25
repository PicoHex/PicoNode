# PicoWeb WebAPI Framework Design

**Goal:** Extend `PicoWeb` from thin host glue to a batteries-included WebAPI framework by adding `PicoDI` (concrete container) + `PicoJetson` (JSON) as dependencies.

**Principle:** `PicoNode.Web` stays unchanged — no new dependencies. All concrete implementations are pulled in at the `PicoWeb` level.

**New:** PicoJetson 2026.2.1+ supports `[PicoJsonSerializable]` attribute — the attribute scanning mechanism needed for AOT-compatible cross-assembly serialization registration.

---

## Dependency Change

```xml
<!-- PicoWeb.csproj — add: -->
<PackageReference Include="PicoDI" Version="2026.6.2" />
<PackageReference Include="PicoJetson" Version="2026.1.9" />
<!-- Keep existing: -->
<PackageReference Include="PicoCfg.Abs" Version="2026.6.2" />
<ProjectReference Include="..\PicoNode.Web\PicoNode.Web.csproj" />
```

## New Types

### 1. `WebApiBuilder` — 入口构建器

```csharp
namespace PicoWeb;

public sealed class WebApiBuilder
{
    private readonly SvcContainer _container = new();
    private WebAppOptions? _options;
    private JsonOptions? _jsonOptions;

    public WebApiBuilder ConfigureJson(Action<JsonOptions> configure)
    {
        _jsonOptions ??= new JsonOptions();
        configure(_jsonOptions);
        return this;
    }

    public WebApiBuilder ConfigureApp(Action<WebAppOptions> configure)
    {
        _options ??= new WebAppOptions();
        configure(_options);
        return this;
    }

    // 服务注册 — 委托给 PicoDI 扩展方法
    public WebApiBuilder RegisterSingleton<TService, TImpl>()
        where TImpl : TService
    {
        _container.RegisterSingleton(typeof(TService), typeof(TImpl));
        return this;
    }

    public WebApiBuilder RegisterScoped<TService, TImpl>()
        where TImpl : TService
    {
        _container.RegisterScoped(typeof(TService), typeof(TImpl));
        return this;
    }

    public WebApiBuilder RegisterTransient<TService, TImpl>()
        where TImpl : TService
    {
        _container.RegisterTransient(typeof(TService), typeof(TImpl));
        return this;
    }

    // 构建
    public WebApiApp Build()
    {
        _container.Build();
        var app = new WebApp(_container, _options ?? new());
        return new WebApiApp(app, _jsonOptions);
    }
}
```

### 2. `WebApiApp` — 运行时的 API 入口

```csharp
namespace PicoWeb;

public sealed class WebApiApp : IAsyncDisposable
{
    private readonly WebApp _app;
    private WebServer? _server;

    internal WebApiApp(WebApp app, JsonOptions? jsonOptions)
    {
        _app = app;
        AppSerializationOptions.Default = jsonOptions ?? new JsonOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
    }

    public WebApiApp MapGet(string pattern, Delegate handler)
    {
        _app.MapGet(pattern, handler);
        return this;
    }

    public WebApiApp MapPost(string pattern, Delegate handler)
    {
        _app.MapPost(pattern, handler);
        return this;
    }

    public WebApiApp MapPut(string pattern, Delegate handler)
    {
        _app.MapPut(pattern, handler);
        return this;
    }

    public WebApiApp MapDelete(string pattern, Delegate handler)
    {
        _app.MapDelete(pattern, handler);
        return this;
    }

    public async Task RunAsync(string uri, CancellationToken ct = default)
    {
        var ep = ParseEndpoint(uri);
        _server = new WebServer(_app, new WebServerOptions { Endpoint = ep });
        await _server.StartAsync(ct);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // 优雅关闭
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    private static IPEndPoint ParseEndpoint(string uri)
    {
        // 支持格式: "http://+:5000", "http://localhost:5000", "https://*:5001"
        var u = new Uri(uri);
        var host = u.Host;
        var port = u.Port > 0 ? u.Port : 80;

        if (host == "+" || host == "*")
            return new IPEndPoint(IPAddress.Any, port);

        // 尝试解析为 IP 地址，否则使用 DNS 解析主机名
        if (!IPAddress.TryParse(host, out var addr))
            addr = Dns.GetHostEntry(host).AddressList[0];
        return new IPEndPoint(addr, port);
    }
}
```

### 3. `Results` — 响应构造器

```csharp
namespace PicoWeb;

public static class Results
{
    public static HttpResponse Json<T>(int statusCode, T value, string? reasonPhrase = null)
    {
        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(value, AppSerializationOptions.Default);
        return new HttpResponse
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase ?? GetDefaultReason(statusCode),
            Headers =
            [
                new KeyValuePair<string, string>(
                    HttpHeaderNames.ContentType,
                    "application/json; charset=utf-8"),
            ],
            Body = json,
        };
    }

    public static HttpResponse Text(int statusCode, string body, string? reasonPhrase = null) =>
        WebResults.Text(statusCode, body, reasonPhrase ?? "");

    public static HttpResponse Empty(int statusCode, string? reasonPhrase = null) =>
        WebResults.Empty(statusCode, reasonPhrase ?? "");

    private static string GetDefaultReason(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        400 => "Bad Request",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "",
    };
}

internal static class AppSerializationOptions
{
    public static JsonOptions Default { get; set; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
```

### 4. 请求体解析 — 透明手动绑定

不引入隐式绑定。用户在 handler 中通过 `ctx.Request.BodyStream` 手动反序列化：

```csharp
api.MapPost("/api/users", async (WebContext ctx, CancellationToken ct) =>
{
    var dto = await PicoJetson.JsonSerializer.DeserializeFromStreamAsync<CreateUserDto>(
        ctx.Request.BodyStream, null, ct);
    // ... 处理 dto
    return Results.Json(201, result);
});
```

---

## 使用示例

```csharp
using PicoWeb;

var builder = new WebApiBuilder();

// 配置
builder.ConfigureJson(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.ConfigureApp(o => o.ServerHeader = "MyApi/1.0");

// 注册服务
builder.RegisterScoped<IUserService, UserService>();
builder.RegisterSingleton<ILogger, Logger>();

// 构建
var api = builder.Build();

// 路由
api.MapGet("/api/users/{id}", async (WebContext ctx, IUserService service) =>
{
    var user = await service.GetByIdAsync(ctx.RouteValues["id"]);
    return Results.Json(200, user);
});

// 启动
await api.RunAsync("http://+:5000");
```

---

## 不变的部分

| 组件 | 说明 |
|------|------|
| `PicoNode.Abs` | 零变化 |
| `PicoNode` | 零变化 |
| `PicoNode.Http` | 零变化 |
| `PicoNode.Web` | 零变化 — `PicoDI.Abs` 接口依赖不变 |
| `PicoWeb.WebServer` | 不变 — WebApiApp 仍使用它启动 |

## 影响

| 文件 | 变更 |
|------|------|
| `PicoWeb.csproj` | 增加 `PicoDI` + `PicoJetson` 包引用 |
| `PicoWeb/WebApiBuilder.cs` | 新建 |
| `PicoWeb/WebApiApp.cs` | 新建 |
| `PicoWeb/Results.cs` | 新建 |
| `PicoWeb/GlobalUsings.cs` | 新增 `global using PicoDI;` + `global using PicoJetson;` + `global using PicoNode.Abs;` |

总新增约 200 行代码，下层 5 个模块零变更。
