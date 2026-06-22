# RateLimit Middleware Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add token-bucket rate limiting middleware with pluggable storage (IRateLimitStore + in-memory default).

**Architecture:** New `RateLimitMiddleware` with 2 factory overloads (DI-resolved and explicit). Token-bucket algorithm in `InMemoryRateLimitStore` using `ConcurrentDictionary` + `object lock` per key + `Timer` cleanup. `RateLimitResult` provides bucket state for response headers.

**Tech Stack:** .NET 10, `TUnit` for tests, `PicoNode.Web` library

## Global Constraints

- TDD: test first, watch fail, implement minimal code, watch pass, commit
- AOT-safe: no reflection, no `dynamic`, no `Type.GetType`
- Follow existing patterns: `sealed class`, `static Create()` factory, delegates over interfaces, `IDisposable` on in-memory store (matching `InMemorySessionStore`)
- `init`-only properties for models, `ArgumentNullException.ThrowIfNull` for parameter guards

---

### Task 1: Create RateLimitResult and RateLimitState models + IRateLimitStore interface

**Files:**
- Create: `src/PicoNode.Web/RateLimitResult.cs`
- Create: `src/PicoNode.Web/RateLimitState.cs`
- Create: `src/PicoNode.Web/IRateLimitStore.cs`

**Interfaces:**
- Produces: `RateLimitResult` (Allowed, Limit, Remaining, NextAvailableAt, ResetAt), `RateLimitState` (Remaining, Limit, NextAvailableAt), `IRateLimitStore.TryConsumeTokenAsync(string key, CancellationToken)` → `ValueTask<RateLimitResult>`

- [ ] **Step 1: Create RateLimitResult.cs**

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

- [ ] **Step 2: Create RateLimitState.cs**

```csharp
namespace PicoNode.Web;

/// <summary>
/// Injected into WebContext.Items on successful rate limit check.
/// </summary>
public sealed class RateLimitState
{
    public int Remaining { get; init; }
    public int Limit { get; init; }
    public long NextAvailableAt { get; init; }
}
```

- [ ] **Step 3: Create IRateLimitStore.cs**

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

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.Web/RateLimitResult.cs src/PicoNode.Web/RateLimitState.cs src/PicoNode.Web/IRateLimitStore.cs
git commit -m "feat: add RateLimit models and IRateLimitStore interface"
```

---

### Task 2: Create RateLimitOptions

**Files:**
- Create: `src/PicoNode.Web/RateLimitOptions.cs`

**Interfaces:**
- Produces: `RateLimitOptions` — KeySelector delegate, MaxTokens, RefillRate, RefillInterval, FailOpen, CleanupInterval

- [ ] **Step 1: Create RateLimitOptions.cs**

```csharp
namespace PicoNode.Web;

/// <summary>
/// Configuration for <see cref="RateLimitMiddleware"/> and <see cref="InMemoryRateLimitStore"/>.
/// </summary>
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

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/PicoNode.Web/PicoNode.Web.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/PicoNode.Web/RateLimitOptions.cs
git commit -m "feat: add RateLimitOptions for rate limit configuration"
```

---

### Task 3: Add RateLimitState key to WebContextKeys

**Files:**
- Modify: `src/PicoNode.Web/WebContextKeys.cs`

- [ ] **Step 1: Add constant**

```csharp
namespace PicoNode.Web;

public static class WebContextKeys
{
    public const string AuthIdentity = "AuthIdentity";
    public const string RateLimitState = "RateLimitState";
}
```

- [ ] **Step 2: Commit**

```bash
git add src/PicoNode.Web/WebContextKeys.cs
git commit -m "feat: add RateLimitState key to WebContextKeys"
```

---

### Task 4: Create InMemoryRateLimitStore

**Files:**
- Create: `src/PicoNode.Web/InMemoryRateLimitStore.cs`
- Create: `tests/PicoNode.Web.Tests/InMemoryRateLimitStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 (IRateLimitStore, RateLimitResult), Task 2 (RateLimitOptions)
- Produces: `InMemoryRateLimitStore : IRateLimitStore, IDisposable`

- [ ] **Step 1: Write the failing test**

File: `tests/PicoNode.Web.Tests/InMemoryRateLimitStoreTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class InMemoryRateLimitStoreTests
{
    private static RateLimitOptions DefaultOptions => new()
    {
        MaxTokens = 3,
        RefillRate = 1,
        RefillInterval = TimeSpan.FromSeconds(1),
        KeySelector = static _ => "fixed-key",
    };

    [Test]
    public async Task Single_request_is_allowed()
    {
        using var store = new InMemoryRateLimitStore(DefaultOptions);

        var result = await store.TryConsumeTokenAsync("user-1");

        await Assert.That(result.Allowed).IsTrue();
        await Assert.That(result.Remaining).IsEqualTo(2);
        await Assert.That(result.Limit).IsEqualTo(3);
    }

    [Test]
    public async Task MaxTokens_requests_all_allowed()
    {
        using var store = new InMemoryRateLimitStore(DefaultOptions);

        for (int i = 0; i < 3; i++)
        {
            var result = await store.TryConsumeTokenAsync("user-1");
            await Assert.That(result.Allowed).IsTrue();
        }
    }

    [Test]
    public async Task Exceed_max_tokens_gets_rate_limited()
    {
        using var store = new InMemoryRateLimitStore(DefaultOptions);

        for (int i = 0; i < 3; i++)
            await store.TryConsumeTokenAsync("user-1");

        var result = await store.TryConsumeTokenAsync("user-1");

        await Assert.That(result.Allowed).IsFalse();
        await Assert.That(result.Remaining).IsEqualTo(0);
        await Assert.That(result.NextAvailableAt).IsGreaterThan(0);
    }

    [Test]
    public async Task Different_keys_have_independent_buckets()
    {
        using var store = new InMemoryRateLimitStore(DefaultOptions);

        // Exhaust user-1
        for (int i = 0; i < 3; i++)
            await store.TryConsumeTokenAsync("user-1");

        var resultA = await store.TryConsumeTokenAsync("user-1");
        var resultB = await store.TryConsumeTokenAsync("user-2");

        await Assert.That(resultA.Allowed).IsFalse();
        await Assert.That(resultB.Allowed).IsTrue();
    }

    [Test]
    public async Task Bucket_refills_after_interval()
    {
        using var store = new InMemoryRateLimitStore(new RateLimitOptions
        {
            MaxTokens = 1,
            RefillRate = 1,
            RefillInterval = TimeSpan.FromMilliseconds(100),
            KeySelector = static _ => "k",
        });

        await store.TryConsumeTokenAsync("k");
        var resultBefore = await store.TryConsumeTokenAsync("k");
        await Assert.That(resultBefore.Allowed).IsFalse();

        await Task.Delay(150);
        var resultAfter = await store.TryConsumeTokenAsync("k");
        await Assert.That(resultAfter.Allowed).IsTrue();
    }

    [Test]
    public async Task Constructor_throws_on_invalid_max_tokens()
    {
        await Assert.That(() =>
                new InMemoryRateLimitStore(
                    new RateLimitOptions
                    {
                        MaxTokens = 0,
                        KeySelector = static _ => "k",
                    }))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Dispose_throws_on_subsequent_calls()
    {
        var store = new InMemoryRateLimitStore(DefaultOptions);
        store.Dispose();

        await Assert
            .That(async () => await store.TryConsumeTokenAsync("k"))
            .Throws<ObjectDisposedException>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: Build error — `'InMemoryRateLimitStore' could not be found`

- [ ] **Step 3: Create InMemoryRateLimitStore.cs**

File: `src/PicoNode.Web/InMemoryRateLimitStore.cs`

```csharp
namespace PicoNode.Web;

/// <summary>
/// In-memory rate limit store using token-bucket algorithm.
/// Thread-safe per-key via object locks. Timer-based cleanup of expired buckets.
/// </summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore, IDisposable
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly Timer _cleanupTimer;
    private readonly int _maxTokens;
    private readonly int _refillRate;
    private readonly double _refillIntervalTicks;
    private readonly long _cleanupIntervalTicks;
    private int _disposed;

    public InMemoryRateLimitStore(RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxTokens, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.RefillInterval, TimeSpan.Zero);

        _maxTokens = options.MaxTokens;
        _refillRate = options.RefillRate;
        _refillIntervalTicks = options.RefillInterval.Ticks;
        _cleanupIntervalTicks = options.CleanupInterval.Ticks;

        var cleanupInterval = options.CleanupInterval;
        var interval = options.RefillInterval < cleanupInterval
            ? options.RefillInterval
            : cleanupInterval;
        _cleanupTimer = new Timer(
            _ => CleanupExpired(), null, interval, interval);
    }

    public ValueTask<RateLimitResult> TryConsumeTokenAsync(
        string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        var now = DateTimeOffset.UtcNow.Ticks;

        lock (bucket.Lock)
        {
            var result = TryConsume(bucket, now);
            return ValueTask.FromResult(result);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cleanupTimer.Dispose();
        _buckets.Clear();
    }

    private RateLimitResult TryConsume(Bucket bucket, long nowTicks)
    {
        // Refill
        if (bucket.LastAccessTicks > 0 && _refillRate > 0)
        {
            var elapsed = nowTicks - bucket.LastAccessTicks;
            var tokensToAdd = (elapsed / _refillIntervalTicks) * _refillRate;
            bucket.Tokens = Math.Min(bucket.Tokens + tokensToAdd, _maxTokens);
        }

        bucket.LastAccessTicks = nowTicks;

        // Consume
        var allowed = bucket.Tokens >= 1.0;
        if (allowed)
        {
            bucket.Tokens -= 1.0;
        }

        var remaining = (int)Math.Floor(bucket.Tokens);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // NextAvailableAt
        long nextAvailable;
        if (bucket.Tokens >= 1.0)
        {
            nextAvailable = nowUnix;
        }
        else
        {
            var needed = 1.0 - bucket.Tokens;
            var seconds = Math.Ceiling(needed * _refillIntervalTicks / _refillRate / Stopwatch.Frequency);
            nextAvailable = nowUnix + (long)seconds;
        }

        // ResetAt (when bucket is fully refilled)
        var toFill = _maxTokens - bucket.Tokens;
        var resetSeconds = toFill * _refillIntervalTicks / _refillRate / Stopwatch.Frequency;
        var resetAt = nowUnix + (long)Math.Ceiling(resetSeconds);

        return new RateLimitResult
        {
            Allowed = allowed,
            Limit = _maxTokens,
            Remaining = remaining,
            NextAvailableAt = nextAvailable,
            ResetAt = resetAt,
        };
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.Ticks - _cleanupIntervalTicks * 2;

        foreach (var (id, bucket) in _buckets)
        {
            if (bucket.LastAccessTicks < cutoff)
                _buckets.TryRemove(id, out _);
        }
    }

    private sealed class Bucket
    {
        public double Tokens;
        public long LastAccessTicks;
        public readonly object Lock = new();
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: All `InMemoryRateLimitStoreTests` pass

- [ ] **Step 5: Commit**

```bash
git add src/PicoNode.Web/InMemoryRateLimitStore.cs tests/PicoNode.Web.Tests/InMemoryRateLimitStoreTests.cs
git commit -m "feat: add InMemoryRateLimitStore with token-bucket algorithm"
```

---

### Task 5: Create RateLimitMiddleware

**Files:**
- Create: `src/PicoNode.Web/RateLimitMiddleware.cs`
- Create: `tests/PicoNode.Web.Tests/RateLimitMiddlewareTests.cs`

**Interfaces:**
- Consumes: Task 1 (IRateLimitStore), Task 2 (RateLimitOptions), Task 3 (WebContextKeys)
- Produces: `RateLimitMiddleware.Create(RateLimitOptions)` → `WebMiddleware`, `RateLimitMiddleware.Create(IRateLimitStore, RateLimitOptions)` → `WebMiddleware`

- [ ] **Step 1: Write the failing test**

File: `tests/PicoNode.Web.Tests/RateLimitMiddlewareTests.cs`

```csharp
namespace PicoNode.Web.Tests;

public sealed class RateLimitMiddlewareTests
{
    private static RateLimitOptions FixedKeyOptions => new()
    {
        MaxTokens = 3,
        KeySelector = static _ => "test-key",
    };

    [Test]
    public async Task Within_limit_passes_through()
    {
        var store = new InMemoryRateLimitStore(FixedKeyOptions);
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var response = await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Rate_limited_returns_429()
    {
        var store = new InMemoryRateLimitStore(new RateLimitOptions
        {
            MaxTokens = 1,
            KeySelector = static _ => "test-key",
        });
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        // First request — allowed
        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        // Second request — rate limited
        var context2 = WebContext.Create(request);
        var response = await middleware(context2, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(429);
    }

    [Test]
    public async Task Injects_rate_limit_state_on_success()
    {
        var store = new InMemoryRateLimitStore(FixedKeyOptions);
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        var state = context.Items[WebContextKeys.RateLimitState] as RateLimitState;
        await Assert.That(state).IsNotNull();
        await Assert.That(state!.Remaining).IsEqualTo(2);
        await Assert.That(state.Limit).IsEqualTo(3);
    }

    [Test]
    public async Task Adds_rate_limit_headers_on_success()
    {
        var store = new InMemoryRateLimitStore(FixedKeyOptions);
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var response = await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(response.Headers.TryGetValue("X-RateLimit-Limit", out _)).IsTrue();
        await Assert.That(response.Headers.TryGetValue("X-RateLimit-Remaining", out _)).IsTrue();
    }

    [Test]
    public async Task Fail_open_allows_when_store_throws()
    {
        var store = new ThrowingRateLimitStore();
        var options = new RateLimitOptions
        {
            MaxTokens = 1,
            KeySelector = static _ => "k",
            FailOpen = true,
        };
        var middleware = RateLimitMiddleware.Create(store, options);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var response = await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Fail_closed_returns_429_when_store_throws()
    {
        var store = new ThrowingRateLimitStore();
        var options = new RateLimitOptions
        {
            MaxTokens = 1,
            KeySelector = static _ => "k",
            FailOpen = false,
        };
        var middleware = RateLimitMiddleware.Create(store, options);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var response = await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(429);
    }

    [Test]
    public async Task KeySelector_null_falls_back_to_anonymous()
    {
        var store = new InMemoryRateLimitStore(new RateLimitOptions
        {
            MaxTokens = 1,
            KeySelector = static _ => null!,
        });
        var middleware = RateLimitMiddleware.Create(store, new RateLimitOptions
        {
            MaxTokens = 1,
            KeySelector = static _ => null!,
        });

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var ctx1 = WebContext.Create(request);

        // First request with null key = "anonymous"
        await middleware(ctx1, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        // Second request — should be rate limited (same "anonymous" key)
        var ctx2 = WebContext.Create(request);
        var response = await middleware(ctx2, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(429);
    }

    private sealed class ThrowingRateLimitStore : IRateLimitStore
    {
        public ValueTask<RateLimitResult> TryConsumeTokenAsync(string key, CancellationToken ct)
            => throw new InvalidOperationException("store down");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: Build error — `'RateLimitMiddleware' could not be found`

- [ ] **Step 3: Create RateLimitMiddleware.cs**

File: `src/PicoNode.Web/RateLimitMiddleware.cs`

```csharp
namespace PicoNode.Web;

public sealed class RateLimitMiddleware
{
    /// <summary>
    /// Creates rate limit middleware that resolves <see cref="IRateLimitStore"/> from DI.
    /// Returns 503 if the store is not registered.
    /// </summary>
    public static WebMiddleware Create(RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return async (context, next, ct) =>
        {
            if (!context.Services.TryGetService(
                    typeof(IRateLimitStore), out var svc)
                || svc is not IRateLimitStore store)
            {
                return new HttpResponse
                {
                    StatusCode = 503,
                    ReasonPhrase = "Rate limit store not configured",
                };
            }

            return await InvokeCoreAsync(context, next, ct, store, options);
        };
    }

    /// <summary>
    /// Creates rate limit middleware with an explicitly supplied store.
    /// </summary>
    public static WebMiddleware Create(IRateLimitStore store, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        return (context, next, ct) =>
            InvokeCoreAsync(context, next, ct, store, options);
    }

    private static async ValueTask<HttpResponse> InvokeCoreAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken ct,
        IRateLimitStore store,
        RateLimitOptions options)
    {
        // 1. Extract key
        string key;
        try
        {
            var raw = options.KeySelector(context);
            key = raw ?? "anonymous";
            if (key.Length > 256)
                key = key[..256];
            key = key.Replace('\r', '_').Replace('\n', '_');
        }
        catch
        {
            key = "error";
        }

        // 2. Consume token
        RateLimitResult result;
        try
        {
            result = await store.TryConsumeTokenAsync(key, ct);
        }
        catch
        {
            if (options.FailOpen)
            {
                return await next(context, ct);
            }

            return new HttpResponse
            {
                StatusCode = 429,
                ReasonPhrase = "Too Many Requests",
                Headers = new HttpHeaderCollection
                {
                    { "Retry-After", "60" },
                    { "X-RateLimit-Limit", options.MaxTokens.ToString() },
                },
            };
        }

        // 3. Rate limited
        if (!result.Allowed)
        {
            var retryAfter = Math.Max(
                result.NextAvailableAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1);

            return new HttpResponse
            {
                StatusCode = 429,
                ReasonPhrase = "Too Many Requests",
                Headers = new HttpHeaderCollection
                {
                    { "Retry-After", retryAfter.ToString() },
                    { "X-RateLimit-Limit", result.Limit.ToString() },
                },
            };
        }

        // 4. Allowed — inject state
        context.Items[WebContextKeys.RateLimitState] = new RateLimitState
        {
            Remaining = result.Remaining,
            Limit = result.Limit,
            NextAvailableAt = result.NextAvailableAt,
        };

        // 5. Invoke downstream
        var response = await next(context, ct);

        // 6. Add rate limit headers to response
        response.Headers.Add("X-RateLimit-Limit", result.Limit.ToString());
        response.Headers.Add("X-RateLimit-Remaining", result.Remaining.ToString());
        response.Headers.Add("X-RateLimit-Reset", result.ResetAt.ToString());

        return response;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test --project tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj`
Expected: All `RateLimitMiddlewareTests` pass

- [ ] **Step 5: Run full test suite**

Run: `dotnet test --solution PicoNode.slnx`
Expected: All tests pass, no regressions

- [ ] **Step 6: Commit**

```bash
git add src/PicoNode.Web/RateLimitMiddleware.cs tests/PicoNode.Web.Tests/RateLimitMiddlewareTests.cs
git commit -m "feat: add RateLimitMiddleware with token-bucket and DI-resolved store"
```
