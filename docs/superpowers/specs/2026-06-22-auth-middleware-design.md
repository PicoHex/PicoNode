# Auth Middleware for PicoNode.Web

> 2026-06-22 | Spec | Bearer Token authentication middleware

## 1. Overview

Add a `Bearer` token authentication middleware to `PicoNode.Web`, enabling downstream
handlers to access the authenticated user's identity via `WebContext`.

### Motivation

PicoNode.Web currently provides middleware for CORS, compression, caching, session, and
security headers — but no built-in authentication. Auth is required by virtually every
non-trivial web application (AI gateway token validation, dashboard login, API key checks).

### Non-Goals

- **OAuth2/OIDC full flow** — this middleware handles token *validation* only. The actual
  login flow (authorization code, PKCE, refresh) is out of scope.
- **Session-based auth** — already supported via `SessionMiddleware`. This middleware
  targets stateless token auth.
- **Custom auth scheme** — only `Bearer` is supported. Custom schemes can be built with
  `WebMiddleware` directly.

---

## 2. Architecture

```
Request
    │
    ▼
AuthMiddleware
    │
    ├─ Authorization: Bearer <token>?
    │   ├─ Yes → ValidateToken(token) → identity?
    │   │   ├─ Yes → context.Items["AuthIdentity"] = identity
    │   │   └─ No / throws → skip (not injected)
    │   └─ No → skip
    │
    ▼
Next middleware / handler
    │
    ├─ context.GetAuthIdentity() → AuthIdentity?
    │   ├─ Not null → authenticated
    │   └─ null → anonymous
    │
    ▼
Response
```

**Design decisions:**
- Identity is stored in `WebContext.Items` (generic dictionary), not as a typed property.
  This avoids adding a PicoNode.Web → AuthIdentity dependency to `WebContext`'s signature
  and allows other middleware (RateLimit, Audit) to store their own per-request state.
- `ValidateToken` is a delegate, not an interface. Follows the PicoNode.Web pattern of
  using delegates for extensibility (`WebMiddleware`, `WebRequestHandler`).
- Validation failure does NOT return 401 — the middleware silently skips. The downstream
  handler decides whether anonymous access is allowed. This matches how ASP.NET Core's
  authentication middleware works.

---

## 3. Files

| File | Purpose |
|------|---------|
| `src/PicoNode.Web/WebContext.cs` | Add lazy-initialized `Items` dictionary |
| `src/PicoNode.Web/WebContextKeys.cs` | String constants for well-known Items keys |
| `src/PicoNode.Web/AuthOptions.cs` | Configuration with `ValidateToken` delegate |
| `src/PicoNode.Web/AuthIdentity.cs` | Immutable identity model |
| `src/PicoNode.Web/AuthMiddleware.cs` | Sealed class with static `Create()` factory and `GetIdentity()` helper |

---

## 4. API Design

### 4.1 AuthOptions

```csharp
namespace PicoNode.Web;

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

### 4.2 AuthIdentity

```csharp
namespace PicoNode.Web;

public sealed class AuthIdentity
{
    public required string UserId { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();
}
```

All properties are `init`-only — immutable after construction. The `ValidateToken`
delegate is the only creator.

### 4.3 WebContextKeys

```csharp
namespace PicoNode.Web;

public static class WebContextKeys
{
    public const string AuthIdentity = "AuthIdentity";

    // Reserved for future middleware:
    // public const string RateLimitState = "RateLimitState";
}
```

### 4.4 AuthMiddleware

```csharp
namespace PicoNode.Web;

public sealed class AuthMiddleware
{
    public static WebMiddleware Create(AuthOptions options);

    /// <summary>
    /// Retrieves the authenticated identity from the context, or null if not authenticated.
    /// </summary>
    public static AuthIdentity? GetIdentity(WebContext context);
}
```

### 4.5 WebContext Changes

```csharp
// WebContext.cs — add one property:

private Dictionary<string, object?>? _items;

public IDictionary<string, object?> Items
    => _items ??= new Dictionary<string, object?>();
```

### 4.6 Usage

```csharp
// ── Registration ──
app.Use(AuthMiddleware.Create(new AuthOptions
{
    ValidateToken = static async (token, ct) =>
    {
        // Resolve against database, Redis, or in-memory table
        if (token == "admin-key")
            return new AuthIdentity { UserId = "admin", Permissions = ["*"] };
        return null;
    }
}));

// ── Consumption ──
app.MapGet("/api/me", (WebContext ctx, CancellationToken _) =>
{
    var identity = AuthMiddleware.GetIdentity(ctx);
    if (identity is null)
        return ValueTask.FromResult(WebResults.Json(401, """{"error":"unauthorized"}"""));
    return ValueTask.FromResult(WebResults.Json(200, $$"""{"user":"{{identity.UserId}}"}"""));
});
```

---

## 5. Token Parsing

The middleware parses the `Authorization` header following RFC 7235:

1. Read the `Authorization` header value
2. Skip if null or empty
3. Check if it starts with `"Bearer "` (case-insensitive)
4. Split into scheme and credentials parts
5. Truncate at the first comma to strip realm/scope parameters (RFC 7235 §2.1 allows
   `auth-param` after the credentials)

```
Authorization: Bearer sk-ant-api03-xxx
                         ↑
                    token = "sk-ant-api03-xxx"

Authorization: Bearer token123, realm="example"
                         ↑
                    token = "token123"
```

---

## 6. Edge Cases & Error Handling

| Scenario | Behavior |
|----------|----------|
| No `Authorization` header | Skip — identity not injected |
| `Authorization: Basic xxx` | Skip — only Bearer is handled |
| `Authorization: Bearer` (empty token) | Skip — token is empty/whitespace |
| `ValidateToken` returns null | Identity not injected |
| `ValidateToken` throws | Exception caught, identity not injected |
| `ValidateToken` is null at registration | `ArgumentNullException` thrown on startup |
| Multiple `Authorization` headers | First one is used |
| Token with extra params after comma | Truncated at first comma |

---

## 7. Thread Safety

- `WebContext` is created per-request → no concurrent access to `Items`
- `AuthIdentity` is immutable after construction → safe to share across async continuations
- `ValidateToken` delegate may have its own concurrency concerns → caller's responsibility

---

## 8. AOT Compatibility

| Element | Status | Notes |
|---------|:------:|-------|
| `Dictionary<string, object?>` | ✅ | BCL native AOT |
| `string.Split`, `string.StartsWith` | ✅ | |
| Delegate `Func<T, CancellationToken, ValueTask<T>>` | ✅ | Static delegates in AOT |
| `init` properties | ✅ | Existing pattern from `SessionOptions` |
| No `dynamic` / reflection / `Type.GetType` | ✅ | |

---

## 9. Testing Strategy

| Test | What |
|------|------|
| Valid Bearer token → identity injected | `GetIdentity()?.UserId == "admin"` |
| Missing Authorization header → identity null | `GetIdentity()` returns null |
| Non-Bearer scheme → identity null | `Authorization: Basic xxx` → null |
| Empty Bearer token → identity null | `Authorization: Bearer` → null |
| `ValidateToken` returns null → identity null | Delegate decides token is invalid |
| `ValidateToken` throws → identity null | Downstream still receives the request |
| `ValidateToken` null at registration → throws | `ArgumentNullException` |
| Identity immutable after construction | Verify `init`-only properties |
| Items dictionary lazy-allocated | No allocation when Auth header absent |
| Token with comma → truncated correctly | `Bearer abc, realm=x` → token = "abc" |
