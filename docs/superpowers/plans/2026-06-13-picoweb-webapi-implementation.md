# PicoWeb WebAPI Framework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `PicoWeb` from thin host glue to a batteries-included WebAPI framework with `PicoDI` (concrete container) + `PicoJetson` (JSON serialization).

**Architecture:** All new types live in `src/PicoWeb/`. `PicoNode.Web` and all lower layers stay unchanged — zero modifications. The `WebApiBuilder` configures a `SvcContainer` + `WebAppOptions` + `JsonOptions`, then `Build()` produces a `WebApiApp` that wraps `WebApp` and provides a `RunAsync()` entry point.

**Tech Stack:** .NET 10, C# 13, PicoDI, PicoJetson (2026.2.1+, `[PicoJsonSerializable]`), PicoNode.Web

**Three-Way Endpoint Registration (design):**
- `MapXX` — `PicoWeb.Gen` scans `app.MapGet/MapPost` delegate signatures
- **Attribute** — `Controllers.Gen` scans `[HttpGet]/[HttpPost]` methods
- **Convention** — `Controllers.Gen` scans `Controllers/` folder, method name prefix (`Get` → GET, `Post` → POST)

All three generate the same: `[assembly: PicoJsonSerializable(typeof(T))]` + endpoint stub code.

---

## File Map

| File | Change |
|------|--------|
| `src/PicoWeb/PicoWeb.csproj` | Add `PicoDI` + `PicoJetson` package references |
| `src/PicoWeb/GlobalUsings.cs` | Add `global using PicoDI;` + `global using PicoJetson;` + `global using PicoNode.Abs;` |
| `src/PicoWeb/WebApiBuilder.cs` | Create — fluent builder for container + JSON + app config |
| `src/PicoWeb/WebApiApp.cs` | Create — API runtime (routes + RunAsync) |
| `src/PicoWeb/Results.cs` | Create — `Results.Json<T>()`, `Results.Text()`, `Results.Empty()` |
| `tests/PicoWeb.Tests/PicoWeb.Tests.csproj` | New TUnit test project |
| `tests/PicoWeb.Tests/ResultsTests.cs` | Create — tests for Results class |
| `tests/PicoWeb.Tests/WebApiBuilderTests.cs` | Create |
| `tests/PicoWeb.Tests/WebApiAppTests.cs` | Create |

---

### Task 1: Update project dependencies and global usings

**Files:**
- Modify: `src/PicoWeb/PicoWeb.csproj`
- Modify: `src/PicoWeb/GlobalUsings.cs`

- [ ] **Step 1: Add package references to PicoWeb.csproj**

```xml
<!-- src/PicoWeb/PicoWeb.csproj — add after existing PackageReference items -->
<PackageReference Include="PicoDI" Version="2026.6.7" />
<PackageReference Include="PicoJetson" Version="2026.1.9" />

<ItemGroup>
    <InternalsVisibleTo Include="PicoWeb.Tests" />
</ItemGroup>
```

- [ ] **Step 2: Add global usings**

```csharp
// src/PicoWeb/GlobalUsings.cs — add after existing lines:
global using PicoDI;
global using PicoJetson;
global using PicoNode.Abs;
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/PicoWeb/PicoWeb.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/PicoWeb/PicoWeb.csproj src/PicoWeb/GlobalUsings.cs
git commit -m "deps: add PicoDI and PicoJetson to PicoWeb"
```

---

### Task 1a: Create test project

**Files:**
- Create: `tests/PicoWeb.Tests/PicoWeb.Tests.csproj`

- [ ] **Step 1: Create TUnit-based test project**

```bash
mkdir -p tests/PicoWeb.Tests
cat > tests/PicoWeb.Tests/PicoWeb.Tests.csproj << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" Version="1.54.0" />
    <ProjectReference Include="..\..\src\PicoWeb\PicoWeb.csproj" />
    <ProjectReference Include="..\..\src\PicoNode.Web\PicoNode.Web.csproj" />
    <ProjectReference Include="..\..\src\PicoNode.Http\PicoNode.Http.csproj" />
  </ItemGroup>
</Project>
EOF
```

- [ ] **Step 2: Add test project to solution**

```bash
dotnet sln PicoNode.slnx add tests/PicoWeb.Tests/PicoWeb.Tests.csproj
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build tests/PicoWeb.Tests/PicoWeb.Tests.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add tests/PicoWeb.Tests/
git commit -m "test: add PicoWeb.Tests project"
```

---

### Task 2: Create `Results` class

**Files:**
- Create: `src/PicoWeb/Results.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/PicoWeb.Tests/ResultsTests.cs
namespace PicoWeb.Tests;

public sealed class ResultsTests
{
    [Test]
    public async Task Json_returns_serialized_body()
    {
        var response = Results.Json(200, new { Name = "Alice" });
        var body = Encoding.UTF8.GetString(response.Body.Span);
        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers.TryGetValue("Content-Type", out var ct)).IsTrue();
        await Assert.That(ct).IsEqualTo("application/json; charset=utf-8");
        await Assert.That(body).Contains("Alice");
    }

    [Test]
    public async Task Json_defaults_to_camelCase()
    {
        var response = Results.Json(200, new { UserName = "Bob" });
        var body = Encoding.UTF8.GetString(response.Body.Span);
        await Assert.That(body).Contains("userName");
    }
}
```

- [ ] **Step 2: Verify tests fail**

Build and run tests — should fail because `Results` doesn't exist yet.

- [ ] **Step 3: Implement Results class**

```csharp
// src/PicoWeb/Results.cs
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

- [ ] **Step 4: Verify tests pass**

Run: `dotnet test --project tests/PicoWeb.Tests/PicoWeb.Tests.csproj -c Release`

- [ ] **Step 5: Commit**

```bash
git add src/PicoWeb/Results.cs tests/PicoWeb.Tests/ResultsTests.cs
git commit -m "feat: add Results.Json<T> with PicoJetson serialization"
```

---

### Task 3: Create `WebApiBuilder`

**Files:**
- Create: `src/PicoWeb/WebApiBuilder.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/PicoWeb.Tests/WebApiBuilderTests.cs
namespace PicoWeb.Tests;

public sealed class WebApiBuilderTests
{
    [Test]
    public async Task Build_returns_WebApiApp()
    {
        var builder = new WebApiBuilder();
        var api = builder.Build();
        await Assert.That(api).IsNotNull();
    }

    [Test]
    public async Task ConfigureJson_sets_naming_policy()
    {
        var builder = new WebApiBuilder();
        builder.ConfigureJson(o => o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var api = builder.Build();

        // Verify the serialization uses the configured policy
        var response = Results.Json(200, new { UserName = "test" });
        var body = Encoding.UTF8.GetString(response.Body.Span);
        await Assert.That(body).Contains("userName");
    }

}
```

- [ ] **Step 2: Verify tests fail** — `WebApiBuilder` doesn't exist yet.

- [ ] **Step 3: Implement WebApiBuilder**

```csharp
// src/PicoWeb/WebApiBuilder.cs
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

    public WebApiApp Build()
    {
        _container.Build();
        // Apply JSON options before creating WebApiApp
        if (_jsonOptions is not null)
            AppSerializationOptions.Default = _jsonOptions;

        var app = new WebApp(_container, _options ?? new());
        return new WebApiApp(app);
    }
}
```

- [ ] **Step 4: Verify tests pass**

- [ ] **Step 5: Commit**

```bash
git add src/PicoWeb/WebApiBuilder.cs tests/PicoWeb.Tests/WebApiBuilderTests.cs
git commit -m "feat: add WebApiBuilder with DI registration and JSON config"
```

---

### Task 4: Create `WebApiApp`

**Files:**
- Create: `src/PicoWeb/WebApiApp.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/PicoWeb.Tests/WebApiAppTests.cs
using PicoNode.Http;

namespace PicoWeb.Tests;

public sealed class WebApiAppTests
{
    [Test]
    public async Task MapGet_registers_handler_in_WebApp()
    {
        var builder = new WebApiBuilder();
        var api = builder.Build();

        var invoked = false;
        api.MapGet("/hello", (WebContext ctx) =>
        {
            invoked = true;
            return ValueTask.FromResult(Results.Text(200, "ok"));
        });

        // Verify the route is registered by building and sending a request
        var handler = api.GetHandler();
        var context = new RecordingConnectionContext();
        var request = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("GET /hello HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        await handler.OnReceivedAsync(context, request, CancellationToken.None);
        await Assert.That(invoked).IsTrue();
    }

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId => 1;
        public IPEndPoint RemoteEndPoint => new(IPAddress.Loopback, 9999);
        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UtcNow;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.UtcNow;
        public object? UserState { get; set; }
        public string? NegotiatedProtocol => null;
        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
    }
}
```

- [ ] **Step 2: Implement WebApiApp**

```csharp
// src/PicoWeb/WebApiApp.cs
namespace PicoWeb;

public sealed class WebApiApp : IAsyncDisposable
{
    private readonly WebApp _app;
    private WebServer? _server;

    internal WebApiApp(WebApp app)
    {
        _app = app;
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
            // graceful shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    /// <summary>Builds the handler for test access.</summary>
    internal ITcpConnectionHandler GetHandler() => _app.Build();

    private static IPEndPoint ParseEndpoint(string uri)
    {
        var u = new Uri(uri);
        var host = u.Host;
        var port = u.Port > 0 ? u.Port : 80;

        if (host == "+" || host == "*")
            return new IPEndPoint(IPAddress.Any, port);

        if (!IPAddress.TryParse(host, out var addr))
            addr = Dns.GetHostEntry(host).AddressList[0];
        return new IPEndPoint(addr, port);
    }
}
```

- [ ] **Step 3: Verify tests pass**

- [ ] **Step 4: Commit**

```bash
git add src/PicoWeb/WebApiApp.cs tests/PicoWeb.Tests/WebApiAppTests.cs
git commit -m "feat: add WebApiApp with route registration and RunAsync"
```

---

### Task 5: Update sample to use WebApiBuilder

**Files:**
- Modify: `samples/PicoWeb.Samples/Program.cs`

- [ ] **Step 1: Update sample**

```csharp
// samples/PicoWeb.Samples/Program.cs
using PicoWeb;

var api = new WebApiBuilder()
    .ConfigureApp(o => o.ServerHeader = "PicoWeb.Showcase")
    .Build();

api.MapGet("/api/showcase", (WebContext ctx) =>
    Results.Json(200, new { status = "ok", path = ctx.Path }));

await api.RunAsync("http://+:7004");
```

- [ ] **Step 2: Build sample**

Run: `dotnet build samples/PicoWeb.Samples/PicoWeb.Samples.csproj -c Release`

- [ ] **Step 3: Commit**

```bash
git add samples/PicoWeb.Samples/Program.cs
git commit -m "samples: use WebApiBuilder in PicoWeb.Samples"
```

---

### Task 6: Final build and test

- [ ] **Step 1: Full solution build**

```bash
dotnet build PicoNode.slnx -c Release
```

Expected: 0 errors.

- [ ] **Step 2: Run all tests**

```bash
dotnet test --project tests/PicoNode.Tests/PicoNode.Tests.csproj -c Release --no-build
dotnet test --project tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj -c Release --no-build
dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -c Release --no-build
dotnet test --project tests/PicoWeb.Tests/PicoWeb.Tests.csproj -c Release --no-build
dotnet test --project tests/PicoNode.Smoke/PicoNode.Smoke.csproj -c Release --no-build
```

Expected: All tests pass.

- [ ] **Step 3: Verify git log**

```bash
git log --oneline -6
```

Expected: 5-6 commits covering all changes.
