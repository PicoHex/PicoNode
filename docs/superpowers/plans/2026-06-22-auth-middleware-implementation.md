# Auth Middleware Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Bearer token authentication middleware that injects `AuthIdentity` into `WebContext.Items`.

**Architecture:** New `AuthMiddleware` sealed class with `Create(AuthOptions)` factory method returning `WebMiddleware`. Identity stored in new `WebContext.Items` dictionary. Downstream consumers call `AuthMiddleware.GetIdentity(ctx)`.

**Tech Stack:** .NET 10, `TUnit` for tests, `PicoNode.Web` library

## Global Constraints

- TDD: test first, watch fail, implement minimal code, watch pass, commit
- AOT-safe: no reflection, no `dynamic`, no `Type.GetType`
- Follow existing patterns: `sealed class`, `static Create()` factory, delegates over interfaces
- `init`-only properties for models, `ArgumentNullException.ThrowIfNull` for parameter guards

---

### Task 1: Add Items dictionary to WebContext

**Files:**
- Modify: `src/PicoNode.Web/WebContext.cs`
- Create: `tests/PicoNode.Web.Tests/WebContextItemsTests.cs`

**Interfaces:**
- Produces: `public IDictionary<string, object?> Items` — lazy-initialized per-request dictionary

- [ ] **Step 1: Write the failing test**

File: `tests/PicoNode.Web.Tests/WebContextItemsTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class WebContextItemsTests
{
    [Test]
    public async Task Items_is_lazy_initialized()
    {
        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var items = context.Items;

        await Assert.That(items).IsNotNull();
        await Assert.That(items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Items_supports_add_and_get()
    {
        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        context.Items["key"] = "value";
        var value = context.Items["key"];

        await Assert.That(value).IsEqualTo("value");
    }

    [Test]
    public async Task Items_stores_null_values()
    {
        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        context.Items["key"] = null;

        await Assert.That(context.Items.ContainsKey("key")).IsTrue();
        await Assert.That(context.Items["key"]).IsNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: Build error — `'WebContext' does not contain a definition for 'Items'`

- [ ] **Step 3: Implement Items in WebContext.cs**

File: `src/PicoNode.Web/WebContext.cs`

Add after the `Services` property:

```csharp
private Dictionary<string, object?>? _items;

public IDictionary<string, object?> Items
    => _items ??= new Dictionary<string, object?>();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: `WebContextItemsTests` pass

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.Web/WebContext.cs tests/PicoNode.Web.Tests/WebContextItemsTests.cs
git commit -m "feat: add Items dictionary to WebContext for per-request middleware state"
```

---

### Task 2: Create WebContextKeys

**Files:**
- Create: `src/PicoNode.Web/WebContextKeys.cs`

**Interfaces:**
- Produces: `WebContextKeys.AuthIdentity` — string constant for well-known Items key

- [ ] **Step 1: Create WebContextKeys.cs**

File: `src/PicoNode.Web/WebContextKeys.cs`

```csharp
namespace PicoNode.Web;

/// <summary>
/// Well-known keys for <see cref="WebContext.Items"/>.
/// </summary>
public static class WebContextKeys
{
    public const string AuthIdentity = "AuthIdentity";
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/WebContextKeys.cs
git commit -m "feat: add WebContextKeys with AuthIdentity constant"
```

---

### Task 3: Create AuthIdentity model

**Files:**
- Create: `src/PicoNode.Web/AuthIdentity.cs`

**Interfaces:**
- Produces: `AuthIdentity` — sealed class with `UserId` (required init), `Permissions` (IReadOnlyList), `Metadata` (IReadOnlyDictionary)

- [ ] **Step 1: Create AuthIdentity.cs**

File: `src/PicoNode.Web/AuthIdentity.cs`

```csharp
namespace PicoNode.Web;

/// <summary>
/// Authenticated user identity injected by <see cref="AuthMiddleware"/>.
/// All properties are init-only — immutable after construction.
/// </summary>
public sealed class AuthIdentity
{
    public required string UserId { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/AuthIdentity.cs
git commit -m "feat: add AuthIdentity model for auth middleware"
```

---

### Task 4: Create AuthOptions

**Files:**
- Create: `src/PicoNode.Web/AuthOptions.cs`

**Interfaces:**
- Produces: `AuthOptions` — sealed class with `ValidateToken` delegate (Func<string, CancellationToken, ValueTask<AuthIdentity?>>)

- [ ] **Step 1: Create AuthOptions.cs**

File: `src/PicoNode.Web/AuthOptions.cs`

```csharp
namespace PicoNode.Web;

/// <summary>
/// Configuration for <see cref="AuthMiddleware"/>.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// Validates a Bearer token and returns an identity, or null if invalid.
    /// Exceptions are caught and treated as validation failures (identity not injected).
    /// </summary>
    public required Func<string, CancellationToken, ValueTask<AuthIdentity?>> ValidateToken
    {
        get;
        init;
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/AuthOptions.cs
git commit -m "feat: add AuthOptions for auth middleware configuration"
```

---

### Task 5: Create AuthMiddleware

**Files:**
- Create: `src/PicoNode.Web/AuthMiddleware.cs`
- Create: `tests/PicoNode.Web.Tests/AuthMiddlewareTests.cs`

**Interfaces:**
- Consumes: Task 1 (WebContext.Items), Task 2 (WebContextKeys), Task 3 (AuthIdentity), Task 4 (AuthOptions)
- Produces: `AuthMiddleware.Create(AuthOptions)` → `WebMiddleware`, `AuthMiddleware.GetIdentity(WebContext)` → `AuthIdentity?`

- [ ] **Step 1: Write the failing test**

File: `tests/PicoNode.Web.Tests/AuthMiddlewareTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class AuthMiddlewareTests
{
    [Test]
    public async Task Valid_bearer_token_injects_identity()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (token, ct) =>
                ValueTask.FromResult<AuthIdentity?>(
                    token == "valid-token"
                        ? new AuthIdentity { UserId = "user-1" }
                        : null),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer valid-token",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        var identity = AuthMiddleware.GetIdentity(context);
        await Assert.That(identity).IsNotNull();
        await Assert.That(identity!.UserId).IsEqualTo("user-1");
    }

    [Test]
    public async Task Missing_authorization_header_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                throw new InvalidOperationException("should not be called"),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }

    [Test]
    public async Task Non_bearer_scheme_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                throw new InvalidOperationException("should not be called"),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Basic dXNlcjpwYXNz",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }

    [Test]
    public async Task ValidateToken_returns_null_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                ValueTask.FromResult<AuthIdentity?>(null),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer any-token",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }

    [Test]
    public async Task ValidateToken_throws_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                throw new InvalidOperationException("db down"),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer token",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }

    [Test]
    public async Task Bearer_scheme_is_case_insensitive()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (token, ct) =>
                ValueTask.FromResult<AuthIdentity?>(
                    token == "tk" ? new AuthIdentity { UserId = "u" } : null),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "bearer tk",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)?.UserId).IsEqualTo("u");
    }

    [Test]
    public async Task Token_with_comma_truncates_at_first_comma()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (token, ct) =>
                ValueTask.FromResult<AuthIdentity?>(
                    token == "abc123" ? new AuthIdentity { UserId = "u" } : null),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer abc123, realm=\"example\"",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)?.UserId).IsEqualTo("u");
    }

    [Test]
    public async Task Empty_token_after_bearer_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                throw new InvalidOperationException("should not be called"),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer ",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: Build error — `'AuthMiddleware' could not be found`

- [ ] **Step 3: Create AuthMiddleware.cs**

File: `src/PicoNode.Web/AuthMiddleware.cs`

```csharp
namespace PicoNode.Web;

/// <summary>
/// Bearer token authentication middleware.
/// Extracts the token from the <c>Authorization</c> header and calls
/// <see cref="AuthOptions.ValidateToken"/> to resolve an identity.
/// The identity is stored in <see cref="WebContext.Items"/> under
/// <see cref="WebContextKeys.AuthIdentity"/>.
/// </summary>
public sealed class AuthMiddleware
{
    /// <summary>
    /// Creates a Bearer token authentication middleware.
    /// </summary>
    /// <param name="options">Configuration with token validation delegate.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="options"/> or <paramref name="options.ValidateToken"/> is null.
    /// </exception>
    public static WebMiddleware Create(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ValidateToken);

        return async (context, next, ct) =>
        {
            if (context.Request.Headers.TryGetValue("Authorization", out var header))
            {
                var parts = header.Split(' ', 2);
                if (parts.Length == 2
                    && string.Equals(parts[0], "Bearer", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = parts[1];
                    if (raw.Length == 0)
                    {
                        // Empty token after "Bearer " — skip
                        var response = await next(context, ct);
                        return response;
                    }

                    var comma = raw.IndexOf(',');
                    var token = comma >= 0 ? raw.Substring(0, comma) : raw;

                    try
                    {
                        var identity = await options.ValidateToken(token, ct);
                        if (identity is not null)
                            context.Items[WebContextKeys.AuthIdentity] = identity;
                    }
                    catch
                    {
                        // Validation failed — identity not injected.
                        // Downstream decides whether anonymous access is allowed.
                    }
                }
            }

            return await next(context, ct);
        };
    }

    /// <summary>
    /// Retrieves the authenticated identity from the context, or null if not authenticated.
    /// </summary>
    public static AuthIdentity? GetIdentity(WebContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(WebContextKeys.AuthIdentity, out var v)
            && v is AuthIdentity identity)
            return identity;

        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: All `AuthMiddlewareTests` pass

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --solution PicoNode.slnx`
Expected: All tests pass, no regressions

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.Web/AuthMiddleware.cs tests/PicoNode.Web.Tests/AuthMiddlewareTests.cs
git commit -m "feat: add AuthMiddleware with Bearer token validation"
```
