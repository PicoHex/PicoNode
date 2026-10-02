namespace PicoNode.Web.Tests;

public sealed class RateLimitMiddlewareTests
{
    private static RateLimitOptions FixedKeyOptions =>
        new() { MaxTokens = 3, KeySelector = static _ => "test-key" };

    [Test]
    public async Task Within_limit_passes_through()
    {
        var store = new InMemoryRateLimitStore(FixedKeyOptions);
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var response = await middleware(
            context,
            (ctx, ct) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Rate_limited_returns_429()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions { MaxTokens = 1, KeySelector = static _ => "test-key" }
        );
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        await middleware(
            context,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        var context2 = WebContext.Create(request);
        var response = await middleware(
            context2,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(429);
    }

    [Test]
    public async Task Injects_rate_limit_state_on_success()
    {
        var store = new InMemoryRateLimitStore(FixedKeyOptions);
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        await middleware(
            context,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

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

        var response = await middleware(
            context,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

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
            await middleware(
                ctx,
                (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
                CancellationToken.None
            );
        }

        // 4th request — rate limited
        var limitedCtx = WebContext.Create(request);
        var response = await middleware(
            limitedCtx,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

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

        var response = await middleware(
            context,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

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

        var response = await middleware(
            context,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(429);
    }

    [Test]
    public async Task KeySelector_null_falls_back_to_anonymous()
    {
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions { MaxTokens = 1, KeySelector = static _ => null! }
        );
        var middleware = RateLimitMiddleware.Create(
            store,
            new RateLimitOptions { MaxTokens = 1, KeySelector = static _ => null! }
        );

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var ctx1 = WebContext.Create(request);

        await middleware(
            ctx1,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        var ctx2 = WebContext.Create(request);
        var response = await middleware(
            ctx2,
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(429);
    }

    [Test]
    public async Task KeySelector_throwing_fails_open()
    {
        // A throwing KeySelector must not land every failing request in a shared
        // "error" bucket — an attacker could exhaust that bucket (MaxTokens=1
        // below) and 429 every subsequent request whose key derivation fails.
        // Fail open per-request instead: BOTH requests must pass through.
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions { MaxTokens = 1, KeySelector = static _ => "k" }
        );
        var options = new RateLimitOptions
        {
            MaxTokens = 1,
            KeySelector = static _ => throw new InvalidOperationException("no key"),
        };
        var middleware = RateLimitMiddleware.Create(store, options);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var callCount = 0;
        WebRequestHandler next = (_, _) =>
        {
            callCount++;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var response1 = await middleware(WebContext.Create(request), next, CancellationToken.None);
        var response2 = await middleware(WebContext.Create(request), next, CancellationToken.None);

        await Assert.That(response1.StatusCode).IsEqualTo(200);
        await Assert
            .That(response2.StatusCode)
            .IsEqualTo(200)
            .Because(
                "the second erroring request must not be 429'd by a shared "
                    + "error bucket exhausted by the first"
            );
        await Assert.That(callCount).IsEqualTo(2);
    }

    [Test]
    public async Task Downstream_Limit_Headers_Are_Not_Duplicated()
    {
        // The downstream handler already wrote X-RateLimit-Limit; the middleware
        // must not append a second one. Group-level "first writer wins": the
        // handler's value (7) survives, the middleware's limit is dropped.
        var store = new InMemoryRateLimitStore(FixedKeyOptions);
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var response = await middleware(
            context,
            (_, _) =>
            {
                var downstream = new HttpResponse { StatusCode = 200 };
                downstream.Headers.Add("X-RateLimit-Limit", "7");
                return ValueTask.FromResult(downstream);
            },
            CancellationToken.None
        );

        var limitValues = new List<string>();
        foreach (var value in response.Headers.GetValues("X-RateLimit-Limit"))
            limitValues.Add(value);

        await Assert.That(limitValues.Count).IsEqualTo(1);
        await Assert.That(limitValues[0]).IsEqualTo("7");
    }

    [Test]
    public async Task Rejected_Body_Keeps_The_Legacy_Shape()
    {
        // Characterization test for the extraction: the 429 body/headers must stay
        // byte-for-byte identical to the legacy middleware's. RefillRate=0 (fixed
        // window) makes NextAvailableAt stable instead of racing a 1 s refill.
        var options = new RateLimitOptions
        {
            MaxTokens = 1,
            RefillRate = 0,
            RefillInterval = TimeSpan.FromSeconds(1),
            KeySelector = static _ => "test-key",
        };
        var store = new InMemoryRateLimitStore(options);
        var middleware = RateLimitMiddleware.Create(store, options);

        var request = new HttpRequest { Method = "GET", Target = "/" };

        // Consume the only token.
        await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        // The rejected result the middleware will build its body from.
        var denied = await store.TryConsumeTokenAsync("test-key");
        await Assert.That(denied.Allowed).IsFalse();

        var expectedRetryAfter = Math.Max(
            denied.NextAvailableAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            1
        );

        // Next request is rate limited.
        var response = await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(429);
        await Assert
            .That(response.Headers["Retry-After"])
            .IsEqualTo(expectedRetryAfter.ToString());
        await Assert.That(response.Headers["X-RateLimit-Limit"]).IsEqualTo("1");
        await Assert.That(response.Headers["Content-Type"]).IsEqualTo("application/json");
        await Assert
            .That(Encoding.UTF8.GetString(response.Body.Span))
            .IsEqualTo($$"""{"error":"rate-limited","retryAfter":{{expectedRetryAfter}}}""");
    }

    private sealed class ThrowingRateLimitStore : IRateLimitStore
    {
        public ValueTask<RateLimitResult> TryConsumeTokenAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("store down");
    }
}
