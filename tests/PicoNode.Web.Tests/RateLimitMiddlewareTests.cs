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

        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

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
    public async Task Rate_limited_response_has_Json_body_and_content_type()
    {
        var store = new InMemoryRateLimitStore(FixedKeyOptions);
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        // Exhaust all 3 tokens to force 429
        for (int i = 0; i < 3; i++)
        {
            var ctx = WebContext.Create(request);
            await middleware(ctx, (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
                CancellationToken.None);
        }

        // 4th request — rate limited
        var limitedCtx = WebContext.Create(request);
        var response = await middleware(limitedCtx, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(429);
        await Assert.That(response.Headers.TryGetValue("Content-Type", out var ct)).IsTrue();
        await Assert.That(ct).IsEqualTo("application/json");
        await Assert.That(response.Body.Length).IsGreaterThan(0);
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

        await middleware(ctx1, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

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
