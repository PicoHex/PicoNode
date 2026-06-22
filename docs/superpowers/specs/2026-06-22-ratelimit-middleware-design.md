# RateLimit Middleware for PicoNode.Web

> 2026-06-22 | Spec | Token-bucket rate limiting middleware

## 1. Overview

Add a token-bucket rate limiting middleware to `PicoNode.Web`, enabling per-key
throttling with pluggable storage backends. An in-memory implementation ships out
of the box; third parties implement `IRateLimitStore` for Redis or database backends.

### Motivation

PicoNode.Web currently has no built-in rate limiting. Every public-facing API needs
protection against abuse. AI gateways in particular require per-user/per-key quotas
to prevent runaway costs.

### Non-Goals

- **IP-based geo-blocking** — orthogonal concern, handled by infrastructure.
- **WAF/DDoS protection** — this is application-layer rate limiting, not a firewall.
- **Billing/quotas** — rate limiting throttles request frequency; usage-based quotas
  (token consumption, total calls per month) are a separate concern.

---

## 2. Architecture

```
Request
    │
    ▼
AuthMiddleware         ← 注入 AuthIdentity
    │
    ▼
RateLimitMiddleware    ← 验证速率，注入 RateLimitState
    │
    ├─ KeySelector(ctx) → key
    ├─ store.TryConsumeTokenAsync(key, ct) → RateLimitResult
    ├─ result.Allowed?
    │   ├─ true  → context.Items["RateLimitState"] = state
    │   │           next(ctx, ct) → add X-RateLimit-* headers → return
    │   └─ false → 429 + Retry-After + X-RateLimit-Limit
    │
    ▼
Handler
```

**Design decisions:**
- **Token Bucket algorithm** — supports short bursts (unlike fixed window). Well-suited
  for AI API calls where burst-then-silence is the typical pattern.
- **Pluggable store** — follows the `ISessionStore` pattern. In-memory default with
  `IRateLimitStore` interface for Redis, database, or other backends.
- **Safety-critical** — unlike Session (optional UX feature), rate limiting is a security
  feature. If the store can't be resolved from DI, the middleware returns `503`.
- **Fail-open** — if the store throws an exception, requests are allowed through by
  default (`FailOpen = true`). This prevents a storage outage from blocking all
  legitimate traffic.

---

## 3. Files

| File | Purpose |
|------|---------|
| `src/PicoNode.Web/RateLimitOptions.cs` | Configuration with `KeySelector` delegate |
| `src/PicoNode.Web/RateLimitResult.cs` | Return value from `IRateLimitStore.TryConsumeTokenAsync` |
| `src/PicoNode.Web/RateLimitState.cs` | Per-request state injected into `WebContext.Items` |
| `src/PicoNode.Web/IRateLimitStore.cs` | Storage abstraction interface |
| `src/PicoNode.Web/InMemoryRateLimitStore.cs` | Default implementation (`ConcurrentDictionary` + `Timer`) |
| `src/PicoNode.Web/RateLimitMiddleware.cs` | Sealed class with 2 factory overloads |
| `src/PicoNode.Web/WebContextKeys.cs` | Add `RateLimitState` constant |

---

## 4. API Design

### 4.1 RateLimitOptions

```csharp
namespace PicoNode.Web;

public sealed class RateLimitOptions
{
    /// <summary>
    /// Extracts a rate limit key from the request context.
    /// Returned key is truncated to 256 chars and sanitized (\r\n → _).
    /// Exceptions are caught and key falls back to "error".
    /// </summary>
    public required Func<WebContext, string> KeySelector { get; init; }

    /// <summary>Maximum tokens in the bucket. Must be > 0.</summary>
    public int MaxTokens { get; init; } = 10;

    /// <summary>Tokens added per interval. 0 = single-use bucket (never refills).</summary>
    public int RefillRate { get; init; } = 1;

    /// <summary>Interval between refills. Must be > TimeSpan.Zero.</summary>
    public TimeSpan RefillInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether to allow requests when the store throws an exception.
    /// Default true (fail-open — prefer availability over protection).
    /// </summary>
    public bool FailOpen { get; init; } = true;

    /// <summary>Interval between expired-bucket cleanup scans.</summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);
}
```

### 4.2 RateLimitResult

```csharp
namespace PicoNode.Web;

/// <summary>Result of a token consumption attempt.</summary>
public sealed class RateLimitResult
{
    public required bool Allowed { get; init; }
    public int Limit { get; init; }
    public int Remaining { get; init; }
    public long NextAvailableAt { get; init; }
    public long ResetAt { get; init; }
}
```

### 4.3 RateLimitState

```csharp
namespace PicoNode.Web;

/// <summary>Injected into WebContext.Items on successful rate limit check.</summary>
public sealed class RateLimitState
{
    public int Remaining { get; init; }
    public int Limit { get; init; }
    public long NextAvailableAt { get; init; }
}
```

### 4.4 IRateLimitStore

```csharp
namespace PicoNode.Web;

/// <summary>
/// Storage backend for rate limiting. Implementations must be thread-safe.
/// For distributed scenarios, implement with Redis or an equivalent shared store.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Attempts to consume one token for the given key. Returns a result indicating
    /// whether the request is allowed and current bucket state.
    /// </summary>
    ValueTask<RateLimitResult> TryConsumeTokenAsync(string key, CancellationToken ct);
}
```

The interface lives in `PicoNode.Web` (not a separate `.Abs` package) because it has
only one method and one result type. If it grows to 3+ methods, it will be extracted
into `PicoNode.Web.RateLimit.Abs`.

### 4.5 InMemoryRateLimitStore

```csharp
namespace PicoNode.Web;

/// <summary>
/// In-memory rate limit store using token-bucket algorithm.
/// Thread-safe per-key via object locks. Timer-based cleanup of expired buckets.
/// </summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore, IDisposable
{
    public InMemoryRateLimitStore(RateLimitOptions options);
    public ValueTask<RateLimitResult> TryConsumeTokenAsync(string key, CancellationToken ct);
    public void Dispose();
}
```

Internal bucket structure:
```csharp
private sealed class Bucket
{
    public double Tokens;
    public long LastAccessTicks;
    public readonly object Lock = new();
}
```

- `Tokens` uses `double` precision to support fractional token refills over time.
- `LastAccessTicks` enables cleanup of idle buckets and Retry-After calculation.
- `Lock` serializes refill+consume for correctness; contention is minimal (per-key, one
  request at a time per key in typical HTTP scenarios).

### 4.6 RateLimitMiddleware

```csharp
namespace PicoNode.Web;

public sealed class RateLimitMiddleware
{
    /// <summary>Resolves IRateLimitStore from DI. Returns 503 if not registered.</summary>
    public static WebMiddleware Create(RateLimitOptions options);

    /// <summary>Uses the supplied store directly.</summary>
    public static WebMiddleware Create(IRateLimitStore store, RateLimitOptions options);
}
```

### 4.7 Usage

```csharp
// ── Registration ──
var store = new InMemoryRateLimitStore(new RateLimitOptions { /* dummy */ });
app.Use(AuthMiddleware.Create(new AuthOptions { ... }));
app.Use(RateLimitMiddleware.Create(store, new RateLimitOptions
{
    KeySelector = static ctx =>
        AuthMiddleware.GetIdentity(ctx)?.UserId ?? "anonymous",
    MaxTokens = 30,
    RefillRate = 5,
    RefillInterval = TimeSpan.FromSeconds(1),
}));

// ── Consumption ──
app.MapGet("/api/data", (WebContext ctx, CancellationToken _) =>
{
    var state = ctx.Items[WebContextKeys.RateLimitState] as RateLimitState;
    // state.Remaining, state.Limit available for response customization
    return ValueTask.FromResult(WebResults.Json(200, """{"ok":true}"""));
});
```

---

## 5. Token Bucket Algorithm

### 5.1 Refill calculation

On each `TryConsumeTokenAsync` call:

```
elapsed = nowTicks - bucket.LastAccessTicks
tokensToAdd = (elapsed / intervalTicks) * RefillRate
bucket.Tokens = min(bucket.Tokens + tokensToAdd, MaxTokens)
```

### 5.2 Consume

```
if bucket.Tokens >= 1.0:
    bucket.Tokens -= 1.0
    allowed = true
else:
    allowed = false
```

### 5.3 NextAvailableAt

When `allowed = false`:

```
needed = 1.0 - bucket.Tokens
seconds = Ceiling(needed * intervalSeconds / RefillRate)
NextAvailableAt = now + seconds
```

### 5.4 ResetAt

```
toFill = MaxTokens - bucket.Tokens
seconds = Ceiling(toFill * intervalSeconds / RefillRate)
ResetAt = now + seconds
```

---

## 6. Response Headers

### 6.1 Successful request (200)

| Header | Value |
|--------|-------|
| `X-RateLimit-Limit` | `MaxTokens` |
| `X-RateLimit-Remaining` | `result.Remaining` |
| `X-RateLimit-Reset` | `result.ResetAt` (Unix seconds) |

Headers are added by the middleware after the downstream handler returns, matching the
pattern used by `SessionMiddleware` for `Set-Cookie`.

### 6.2 Rate-limited (429)

| Header | Value |
|--------|-------|
| `Retry-After` | `result.NextAvailableAt - now` (seconds as integer) |
| `X-RateLimit-Limit` | `result.Limit` |

---

## 7. Key Selection

### 7.1 Key extraction

```
raw = options.KeySelector(context)
raw → null? → "anonymous"
raw → >256 chars? → raw[..256]
raw → contains \r or \n? → replace with '_'
KeySelector throws? → "error"
```

### 7.2 Integration with AuthMiddleware

```csharp
KeySelector = static ctx =>
    AuthMiddleware.GetIdentity(ctx)?.UserId  // authenticated
    ?? ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var ip) ? ip
    : "anonymous";
```

### 7.3 API Key-based limiting

```csharp
KeySelector = static ctx =>
    ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) ? key
    : "anonymous";
```

---

## 8. Edge Cases & Error Handling

| Scenario | Behavior |
|----------|----------|
| `KeySelector` returns null | Key = `"anonymous"` |
| `KeySelector` returns > 256 chars | Truncated to 256 |
| `KeySelector` returns `\r\n` | Replaced with `_` |
| `KeySelector` throws | Key = `"error"` |
| `IRateLimitStore` not registered in DI | `503 Service Unavailable` |
| `TryConsumeTokenAsync` throws, `FailOpen = true` | Allowed (default) |
| `TryConsumeTokenAsync` throws, `FailOpen = false` | `429 Too Many Requests` |
| `RefillRate = 0` | Single-use bucket, never refills |
| Bucket expired (idle > CleanupInterval) | Removed by Timer cleanup |
| Timer cleanup races with active request | `TryRemove` fails → bucket kept |
| Multiple requests for same key concurrently | Serialized by per-bucket `object lock` |

---

## 9. Thread Safety

- `InMemoryRateLimitStore` uses `ConcurrentDictionary` for bucket lookup and `object lock`
  per bucket for refill+consume serialization.
- Timer cleanup uses `TryRemove` — fails silently if the bucket is in use by a request
  thread.
- `RateLimitState` is immutable (`init`-only properties).

---

## 10. AOT Compatibility

| Element | Status | Notes |
|---------|:------:|-------|
| `ConcurrentDictionary` | ✅ | BCL native AOT |
| `System.Threading.Timer` | ✅ | .NET 10 AOT compatible |
| `Interlocked`, `object lock` | ✅ | |
| `double` arithmetic | ✅ | |
| `init`-only properties | ✅ | Existing pattern (`SessionOptions`, `AuthIdentity`) |
| No reflection / `dynamic` / `Type.GetType` | ✅ | |

---

## 11. Testing Strategy

| Test | What |
|------|------|
| Single request → allowed | `Allowed = true, Remaining = MaxTokens - 1` |
| `MaxTokens` requests → last one allowed | Sequential requests consume all tokens |
| `MaxTokens + 1` requests → rate limited | `Allowed = false, Retry-After > 0` |
| Wait for refill → allowed again | Sleep > `RefillInterval`, next request succeeds |
| Different keys → independent buckets | Key A exhausted, Key B still has tokens |
| `KeySelector` returns null → `"anonymous"` | Common fallback key |
| `KeySelector` throws → `"error"` | Isolated failure key |
| Store throws + `FailOpen = true` → allowed | Availability over protection |
| Store throws + `FailOpen = false` → 429 | Strict mode |
| `IRateLimitStore` not in DI → 503 | Missing registration is a configuration error |
| Response headers on success | `X-RateLimit-Limit/Remaining/Reset` present |
| Response headers on 429 | `Retry-After` + `X-RateLimit-Limit` present |
| Cleanup removes expired buckets | Bucket unused for > CleanupInterval → removed |
| Bucket refill over time | Sleep then request → tokens replenished correctly |
