namespace PicoWeb.Integration.Tests;

/// <summary>
/// Integration tests for RateLimitMiddleware and InMemoryRateLimitStore.
/// </summary>
public sealed class RateLimitMiddlewareTests
{
    private static WebMiddleware CreateMiddleware(
        IRateLimitStore store,
        RateLimitOptions? options = null
    ) =>
        RateLimitMiddleware.Create(
            store,
            options
                ?? new RateLimitOptions
                {
                    KeySelector = static ctx => "test-key",
                    MaxTokens = 3,
                    RefillRate = 1,
                    RefillInterval = TimeSpan.FromSeconds(60),
                }
        );

    // ── Basic rate limit ───────────────────────────────────────────────

    [Test]
    public async Task First_request_is_allowed()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                KeySelector = static ctx => "test-key",
                MaxTokens = 3,
                RefillRate = 1,
                RefillInterval = TimeSpan.FromSeconds(60),
            }
        );
        var middleware = CreateMiddleware(store);

        var request = new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/ping" };
        var context = WebContext.Create(request);

        var response = await middleware(
            context,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers["x-ratelimit-limit"]).IsEqualTo("3");
        await Assert.That(response.Headers["x-ratelimit-remaining"]).IsEqualTo("2");
    }

    [Test]
    public async Task Exceeding_limit_returns_429()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                KeySelector = static ctx => "test-key",
                MaxTokens = 2,
                RefillRate = 1,
                RefillInterval = TimeSpan.FromSeconds(60),
            }
        );
        var middleware = CreateMiddleware(store);

        var request = new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/ping" };
        var context = WebContext.Create(request);

        // Request 1 → allowed
        var r1 = await middleware(
            context,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );
        await Assert.That(r1.StatusCode).IsEqualTo(200);

        // Request 2 → allowed
        var r2 = await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );
        await Assert.That(r2.StatusCode).IsEqualTo(200);

        // Request 3 → rate limited
        var r3 = await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );
        await Assert.That(r3.StatusCode).IsEqualTo(429);
        await Assert.That(r3.Headers.TryGetValue("retry-after", out _)).IsTrue();
    }

    // ── Headers ────────────────────────────────────────────────────────

    [Test]
    public async Task Rate_limit_headers_are_added_to_allowed_response()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions { KeySelector = static ctx => "headers-test", MaxTokens = 5 }
        );
        var middleware = CreateMiddleware(store);

        var context = WebContext.Create(
            new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/ping" }
        );
        var response = await middleware(
            context,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );

        await Assert.That(response.Headers["x-ratelimit-limit"]).IsEqualTo("5");
        await Assert.That(response.Headers["x-ratelimit-remaining"]).IsEqualTo("4");
        await Assert.That(response.Headers.TryGetValue("x-ratelimit-reset", out _)).IsTrue();
    }

    [Test]
    public async Task Rate_limit_headers_are_added_to_429_response()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions { KeySelector = static ctx => "429-headers", MaxTokens = 1 }
        );
        var middleware = CreateMiddleware(store);

        // First request consumes the only token
        var ctx1 = WebContext.Create(
            new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/ping" }
        );
        await middleware(
            ctx1,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );

        // Second request is rate-limited
        var ctx2 = WebContext.Create(
            new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/ping" }
        );
        var response = await middleware(
            ctx2,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(429);
        await Assert.That(response.Headers["x-ratelimit-limit"]).IsEqualTo("1");
        await Assert.That(response.Headers.TryGetValue("retry-after", out _)).IsTrue();
        await Assert.That(response.Headers["content-type"]).IsEqualTo("application/json");
    }

    // ── State injection ────────────────────────────────────────────────

    [Test]
    public async Task RateLimitState_is_injected_into_context_items()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions { KeySelector = static ctx => "state-test", MaxTokens = 5 }
        );
        var middleware = CreateMiddleware(store);

        var context = WebContext.Create(
            new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/ping" }
        );
        await middleware(
            context,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );

        var state = context.Items[WebContextKeys.RateLimitState] as RateLimitState;
        await Assert.That(state).IsNotNull();
        await Assert.That(state!.Limit).IsEqualTo(5);
        await Assert.That(state.Remaining).IsEqualTo(4);
    }

    // ── Custom key selector ────────────────────────────────────────────

    [Test]
    public async Task Different_keys_have_independent_limits()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                KeySelector = static ctx => "shared",
                MaxTokens = 1,
                RefillRate = 0,
                RefillInterval = TimeSpan.FromHours(1),
            }
        );

        var middlewareA = RateLimitMiddleware.Create(
            store,
            new RateLimitOptions
            {
                KeySelector = static ctx => "key-a",
                MaxTokens = 1,
                RefillInterval = TimeSpan.FromHours(1),
            }
        );
        var middlewareB = RateLimitMiddleware.Create(
            store,
            new RateLimitOptions
            {
                KeySelector = static ctx => "key-b",
                MaxTokens = 1,
                RefillInterval = TimeSpan.FromHours(1),
            }
        );

        var req = new PicoNode.Http.HttpRequest { Method = "GET", Target = "/" };
        var ctxA = WebContext.Create(req);
        var ctxB = WebContext.Create(req);

        // Both get one token each
        var rA1 = await middlewareA(
            ctxA,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );
        var rB1 = await middlewareB(
            ctxB,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );

        await Assert.That(rA1.StatusCode).IsEqualTo(200);
        await Assert.That(rB1.StatusCode).IsEqualTo(200);

        // Second request for A is rate-limited, B was not affected
        var rA2 = await middlewareA(
            WebContext.Create(req),
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );
        await Assert.That(rA2.StatusCode).IsEqualTo(429);

        // B's second request (different key) → also 429 (one token each)
        var rB2 = await middlewareB(
            WebContext.Create(req),
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );
        await Assert.That(rB2.StatusCode).IsEqualTo(429);
    }

    // ── InMemoryRateLimitStore ─────────────────────────────────────────

    [Test]
    public async Task Store_refills_after_interval()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                KeySelector = static ctx => "refill",
                MaxTokens = 1,
                RefillRate = 1,
                RefillInterval = TimeSpan.FromMilliseconds(50),
                CleanupInterval = TimeSpan.FromMilliseconds(100),
            }
        );

        // Consume the only token
        var r1 = await store.TryConsumeTokenAsync("refill");
        await Assert.That(r1.Allowed).IsTrue();
        await Assert.That(r1.Remaining).IsEqualTo(0);

        // Immediate retry → denied
        var r2 = await store.TryConsumeTokenAsync("refill");
        await Assert.That(r2.Allowed).IsFalse();

        // Wait for refill
        await Task.Delay(100);

        var r3 = await store.TryConsumeTokenAsync("refill");
        await Assert.That(r3.Allowed).IsTrue();
        await Assert.That(r3.Remaining).IsEqualTo(0);
    }

    [Test]
    public async Task Store_returns_correct_result_fields()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                KeySelector = static ctx => "fields",
                MaxTokens = 10,
                RefillRate = 2,
                RefillInterval = TimeSpan.FromSeconds(10),
            }
        );

        var result = await store.TryConsumeTokenAsync("fields");

        await Assert.That(result.Allowed).IsTrue();
        await Assert.That(result.Limit).IsEqualTo(10);
        await Assert.That(result.Remaining).IsEqualTo(9);
        await Assert.That(result.NextAvailableAt).IsGreaterThan(0);
        await Assert.That(result.ResetAt).IsGreaterThan(0);
        await Assert.That(result.ResetAt).IsGreaterThanOrEqualTo(result.NextAvailableAt);
    }

    [Test]
    public async Task Store_disposed_throws_on_consume()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions { KeySelector = static ctx => "disp", MaxTokens = 1 }
        );
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            store.TryConsumeTokenAsync("disp", CancellationToken.None).AsTask()
        );
    }

    [Test]
    public async Task Store_truncates_long_keys()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions { KeySelector = static ctx => "long", MaxTokens = 1 }
        );
        var longKey = new string('x', 300);

        // Should not throw
        var result = await store.TryConsumeTokenAsync(longKey);
        await Assert.That(result.Allowed).IsTrue();
    }
}
