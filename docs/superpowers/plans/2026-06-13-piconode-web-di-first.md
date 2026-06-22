# PicoNode.Web DI First Refactoring Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor `PicoNode.Web` from DI-optional to DI-First design, making `ISvcContainer` a required dependency and `Delegate` the primary route handler API.

**Architecture:** Scope creation moves from `ScopeMiddleware` to `WebApp.Build()` lambda. `Delegate` handlers are wrapped into `WebRequestHandler` at registration time via reflection-based parameter resolution. `WebContext.Services` becomes non-nullable. All internal routing machinery (`WebRouter`, `RouteTable`, `BuildPipeline`) remains unchanged.

**Tech Stack:** .NET 10, C# 13, PicoDI.Abs, PicoNode.Web

**Scope:** Only `src/PicoNode.Web/` and `src/PicoWeb/` are modified. No changes to `PicoNode`, `PicoNode.Http`, or `PicoNode.Abs`.

---

## File Map

| File | Change |
|------|--------|
| `src/PicoNode.Web/WebApp.cs` | Constructor, Map(Delegate), Build(), WrapDelegate() |
| `src/PicoNode.Web/WebContext.cs` | `ISvcScope?` → `ISvcScope` (non-nullable, `internal set` stays) |
| `src/PicoNode.Web/Internal/ScopeMiddleware.cs` | Delete (dead code) |
| `src/PicoNode.Web/WebMiddleware.cs` | (No change — `WebRequestHandler` stays) |
| `src/PicoNode.Web/WebRouter.cs` | (No change) |
| `src/PicoNode.Web/WebRoute.cs` | (No change) |
| `src/PicoWeb/WebServer.cs` | Remove `ISvcContainer` constructor, field, and disposal |
| `tests/PicoNode.Web.Tests/` | Update tests to pass container |
| `tests/PicoNode.Smoke/` | Update smoke tests |
| `samples/PicoWeb.Samples/` | Update sample |
| `samples/PicoNode.Samples.Http/` | Update sample to use WebApp if needed |

---

### Task 1: Make `ISvcContainer` required in `WebApp`

**Files:**
- Modify: `src/PicoNode.Web/WebApp.cs:1-28`

Remove the parameterless constructor. Add constructors that require `ISvcContainer`. Store it in a private field for later use in `Build()`.

- [ ] **Step 1: Replace constructors**

```csharp
// Before:
private readonly WebAppOptions? _options;

public WebApp() { }

public WebApp(WebAppOptions options)
{
    ArgumentNullException.ThrowIfNull(options);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxRequestBytes, 0);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StreamingResponseBufferSize, 0);
    _options = options;
}

// After:
private readonly ISvcContainer _container;
private readonly WebAppOptions _options;

public WebApp(ISvcContainer container)
    : this(container, new WebAppOptions()) { }

public WebApp(ISvcContainer container, WebAppOptions options)
{
    ArgumentNullException.ThrowIfNull(container);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxRequestBytes, 0);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StreamingResponseBufferSize, 0);
    _container = container;
    _options = options;
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj -c Release`
Expected: Build fails because existing callers use the old constructors. That's expected — tests and samples will be fixed in later tasks.

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/WebApp.cs
git commit -m "refactor: make ISvcContainer required in WebApp constructor"
```

### Task 2: Add `Map(Delegate)` overloads and `WrapDelegate`

**Files:**
- Modify: `src/PicoNode.Web/WebApp.cs:30-70`

Add the `Delegate`-based route registration methods and the `WrapDelegate` private method that converts `Delegate` → `WebRequestHandler` at registration time.

- [ ] **Step 1: Add `Map(Delegate)` + convenience overloads**

```csharp
// Add after existing field declarations:
private static WebRequestHandler WrapDelegate(Delegate handler)
{
    return async (ctx, ct) =>
    {
        var parameters = handler.Method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type == typeof(WebContext))
                args[i] = ctx;
            else if (type == typeof(CancellationToken))
                args[i] = ct;
            else
                args[i] = ctx.Services.GetService(type);
        }
        var result = handler.DynamicInvoke(args);
        return result switch
        {
            ValueTask<HttpResponse> vt => await vt,
            Task<HttpResponse> t => await t,
            HttpResponse r => r,
            _ => throw new InvalidOperationException(
                $"Handler must return HttpResponse, ValueTask<HttpResponse>, or Task<HttpResponse>.")
        };
    };
}
```

- [ ] **Step 2: Add Delegate overloads**

```csharp
// Add after existing Map/MapGet/... methods:

public WebApp Map(string method, string pattern, Delegate handler)
{
    ArgumentNullException.ThrowIfNull(handler);
    _routes.Add(new()
    {
        Method = method,
        Pattern = pattern,
        Handler = WrapDelegate(handler),
    });
    return this;
}

public WebApp MapGet(string pattern, Delegate handler) => Map("GET", pattern, handler);
public WebApp MapPost(string pattern, Delegate handler) => Map("POST", pattern, handler);
public WebApp MapPut(string pattern, Delegate handler) => Map("PUT", pattern, handler);
public WebApp MapDelete(string pattern, Delegate handler) => Map("DELETE", pattern, handler);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj -c Release`
Expected: Build succeeds (the new overloads don't conflict with existing ones since `Delegate` and `WebRequestHandler` are unrelated types).

- [ ] **Step 4: Commit**

```bash
git add src/PicoNode.Web/WebApp.cs
git commit -m "feat: add Delegate-based route registration and WrapDelegate"
```

### Task 3: Remove old `WebRequestHandler`-based route API

**Files:**
- Modify: `src/PicoNode.Web/WebApp.cs:30-60`

Remove the old `Map(string, string, WebRequestHandler)` and its convenience overloads (`MapGet(WebRequestHandler)`, `MapPost(WebRequestHandler)`, etc.). The `WebRequestHandler` type itself stays (used internally by `WebRouter`/`BuildPipeline`), but it's no longer part of the public API.

- [ ] **Step 1: Remove old overloads**

Delete these methods from `WebApp.cs`:
- `Map(string, string, WebRequestHandler)`
- `MapGet(string, WebRequestHandler)`
- `MapPost(string, WebRequestHandler)`
- `MapPut(string, WebRequestHandler)`
- `MapDelete(string, WebRequestHandler)`

Keep `MapFallback(WebRequestHandler)` for now if needed, or change to `Delegate`:

```csharp
// Before:
public WebApp MapFallback(WebRequestHandler handler)
{
    _fallbackHandler = handler;
    return this;
}

// After:
public WebApp MapFallback(Delegate handler)
{
    ArgumentNullException.ThrowIfNull(handler);
    _fallbackHandler = WrapDelegate(handler);
    return this;
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj -c Release`
Expected: Build fails — existing callers in tests/samples use the old API. That's expected; will be fixed in later tasks.

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/WebApp.cs
git commit -m "refactor: remove WebRequestHandler-based route API, keep Delegate only"
```

### Task 4: Move scope creation to `Build()` + make `Build()` parameterless

**Files:**
- Modify: `src/PicoNode.Web/WebApp.cs:70-120`

Remove the `ISvcContainer?` parameter from `Build()`. Move scope creation from `ScopeMiddleware` into the `Build` lambda, wrapping the pipeline call. Remove the `_middlewares.Insert(0, ScopeMiddleware.Create(...))` call.

- [ ] **Step 1: Replace `Build()` implementation**

```csharp
// Before:
public ITcpConnectionHandler Build(ISvcContainer? container = null)
{
    _middlewares = [.. _middlewares];
    if (container is not null)
    {
        _middlewares.Insert(0, ScopeMiddleware.Create(container));
    }
    var router = new WebRouter(_routes, _fallbackHandler);
    var pipeline = BuildPipeline(router);
    HttpRequestHandler httpHandler = (request, ct) =>
    {
        var context = WebContext.Create(request);
        return pipeline(context, ct);
    };
    return new HttpConnectionHandler(new HttpConnectionHandlerOptions
    {
        RequestHandler = httpHandler,
        // ...
    });
}

// After:
public ITcpConnectionHandler Build()
{
    _middlewares = [.. _middlewares];
    var router = new WebRouter(_routes, _fallbackHandler);
    var pipeline = BuildPipeline(router);

    HttpRequestHandler httpHandler = async (request, ct) =>
    {
        var scope = _container.CreateScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = WebContext.Create(request);
            context.Services = scope;
            return await pipeline(context, ct);
        }
    };
    return new HttpConnectionHandler(new HttpConnectionHandlerOptions
    {
        RequestHandler = httpHandler,
        ServerHeader = _options.ServerHeader,
        Logger = _options.Logger,
        MaxRequestBytes = _options.MaxRequestBytes,
        StreamingResponseBufferSize = _options.StreamingResponseBufferSize,
        RequestTimeout = _options.RequestTimeout,
        WebSocketMessageHandler = _options.WebSocketMessageHandler,
    });
}
```

Note: The `_options` field is now non-null (required by constructor), so null-forgiving operators (`_options?.X ?? default`) can be replaced with direct access.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj -c Release`
Expected: Build fails — existing callers use `Build(container)` with separate container passing.

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/WebApp.cs
git commit -m "refactor: move scope creation to Build lambda, remove optional container parameter"
```

### Task 5: Make `WebContext.Services` non-nullable

**Files:**
- Modify: `src/PicoNode.Web/WebContext.cs:43`

The `Services` property is always set by `Build()` before the pipeline runs, so it can be `ISvcScope` instead of `ISvcScope?`.

- [ ] **Step 1: Change property type**

```csharp
// Before:
public ISvcScope? Services { get; internal set; }

// After:
public ISvcScope Services { get; internal set; } = null!;
```

The `= null!` suppresses the NRT warning. The property is always assigned in the `Build()` lambda before any code reads it.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/WebContext.cs
git commit -m "refactor: make WebContext.Services non-nullable"
```

### Task 6: Remove `ScopeMiddleware.cs`

**Files:**
- Delete: `src/PicoNode.Web/Internal/ScopeMiddleware.cs`

Scope creation is now handled in `WebApp.Build()`, so `ScopeMiddleware` is dead code.

- [ ] **Step 1: Verify no references to `ScopeMiddleware`**

```bash
grep -rn "ScopeMiddleware" src/ --include="*.cs" | grep -v obj/
```

Expected: no hits (the only usage was in `WebApp.Build()` which was removed in Task 4).

- [ ] **Step 2: Delete file**

```bash
rm src/PicoNode.Web/Internal/ScopeMiddleware.cs
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "cleanup: remove ScopeMiddleware, scope creation moved to Build lambda"
```

### Task 7: Simplify `PicoWeb.WebServer`

**Files:**
- Modify: `src/PicoWeb/WebServer.cs`

`WebServer` no longer needs to store or dispose `ISvcContainer` — the container is owned by `WebApp`. Remove the `ISvcContainer` constructor overload, the `_container` field, and container disposal in `DisposeAsync`. Simplify `StartAsync` since `Build()` is now parameterless.

- [ ] **Step 1: Remove `_container` field and `ISvcContainer` constructor**

```csharp
// Before:
private readonly ISvcContainer? _container;

public WebServer(WebApp app, WebServerOptions options)
{
    _app = app;
    _options = options;
    _container = null;
}

public WebServer(WebApp app, WebServerOptions options, ISvcContainer container)
{
    _app = app;
    _options = options;
    _container = container;
}

// After — single constructor, no container:
public WebServer(WebApp app, WebServerOptions options)
{
    ArgumentNullException.ThrowIfNull(app);
    ArgumentNullException.ThrowIfNull(options);
    _app = app;
    _options = options;
}
```

- [ ] **Step 2: Simplify `StartAsync`**

```csharp
// Before:
var handler = _app.Build(_container);

// After:
var handler = _app.Build();
```

- [ ] **Step 3: Remove container disposal from `DisposeAsync`**

```csharp
// Before:
if (_container is not null) await _container.DisposeAsync();

// After: (remove the if block entirely)
```

The caller owns the container lifecycle.

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/PicoWeb/PicoWeb.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/PicoWeb/WebServer.cs
git commit -m "refactor: remove ISvcContainer from WebServer, rely on WebApp constructor"
```

### Task 8: Update tests

**Files:**
- Modify: `tests/PicoNode.Web.Tests/`
- Modify: `tests/PicoNode.Smoke/`

All test code that creates `WebApp` or calls `Build()` must be updated to:
1. Pass a container (use a simple mock implementing `ISvcContainer`)
2. Use `Delegate` overloads instead of `WebRequestHandler`
3. Call `Build()` without container parameter

Use `MockServiceProvider` from `ServiceProviderContractTests.cs` as a template for a simple `ISvcContainer` implementation, or create a minimal inline mock in test files.

- [ ] **Step 1: Create test `ISvcContainer` stub**

```csharp
// In test project, create a minimal container:
internal sealed class TestContainer : ISvcContainer
{
    public ISvcContainer Register(SvcDescriptor descriptor) => this;
    public ISvcScope CreateScope() => new TestScope();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestScope : ISvcScope
{
    public object GetService(Type serviceType) => null!;
    public IReadOnlyList<object> GetServices(Type serviceType) => [];
    public ISvcScope CreateScope() => new TestScope();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 2: Update each test that creates WebApp**

Change:
```csharp
var app = new WebApp();
```

To:
```csharp
var app = new WebApp(new TestContainer());
```

- [ ] **Step 3: Update each test that calls Build(container)**

Change:
```csharp
var handler = app.Build(container);
```

To:
```csharp
var handler = app.Build();
```

- [ ] **Step 4: Update each test that uses old MapGet(WebRequestHandler)**

Change:
```csharp
app.MapGet("/", static (ctx, ct) => ValueTask.FromResult(...));
```

To:
```csharp
app.MapGet("/", async (WebContext ctx) => ...);
```

- [ ] **Step 5: Build and run tests**

Run: `dotnet build tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -c Release`
Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj -c Release --no-build`

- [ ] **Step 6: Update and run smoke tests**

Same pattern for `tests/PicoNode.Smoke/`.

Run: `dotnet build tests/PicoNode.Smoke/PicoNode.Smoke.csproj -c Release`
Run: `dotnet test --project tests/PicoNode.Smoke/PicoNode.Smoke.csproj -c Release --no-build`

- [ ] **Step 7: Commit**

```bash
git add tests/
git commit -m "test: update tests for DI-first WebApp API"
```

### Task 9: Update samples

**Files:**
- Modify: `samples/PicoNode.Samples.Http/Program.cs`
- Modify: `samples/PicoWeb.Samples/Program.cs`
- Modify: `samples/PicoWeb.Samples/ShowcaseApp.cs`

Same pattern as tests — pass container, use Delegate API.

- [ ] **Step 1: Update samples**

For `PicoWeb.Samples`:
```csharp
// Before:
var app = new WebApp(new WebAppOptions { ... });
app.Use(compression.InvokeAsync);
app.MapGet("/api/showcase", static (ctx, _) => ...);

// After:
var container = new SvcContainer();  // or TestContainer for no-DI
var app = new WebApp(container, new WebAppOptions { ... });
app.Use(compression.InvokeAsync);
app.MapGet("/api/showcase", (WebContext ctx) => ...);
```

- [ ] **Step 2: Build samples**

Run: `dotnet build samples/PicoNode.Samples.Http/PicoNode.Samples.Http.csproj -c Release`
Run: `dotnet build samples/PicoWeb.Samples/PicoWeb.Samples.csproj -c Release`

- [ ] **Step 3: Commit**

```bash
git add samples/
git commit -m "samples: update for DI-first WebApp API"
```

### Task 10: Final build and test

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
dotnet test --project tests/PicoNode.Smoke/PicoNode.Smoke.csproj -c Release --no-build
```

Expected: All tests pass.

- [ ] **Step 3: Verify git log**

```bash
git log --oneline -10
```

Expected: 7-10 commits covering all changes.
