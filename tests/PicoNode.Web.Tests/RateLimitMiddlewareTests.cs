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
        await Assert.That(response.ReasonPhrase).IsEqualTo("Too Many Requests");
        await Assert.That(response.Headers["Retry-After"]).IsEqualTo("60");
        await Assert.That(response.Headers["X-RateLimit-Limit"]).IsEqualTo("1");
        await Assert.That(response.Headers["Content-Type"]).IsEqualTo("application/json");
        await Assert
            .That(Encoding.UTF8.GetString(response.Body.Span))
            .IsEqualTo("""{"error":"rate-limited","retryAfter":60}""");
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
        // Spec §3.2.8 / §4 B5: the guard is GROUP-level, not per-header. If the
        // downstream handler wrote ANY X-RateLimit-* header, the middleware must
        // write NONE of X-RateLimit-Limit/-Remaining/-Reset (first writer wins),
        // so the handler's values survive untouched.
        var store = new InMemoryRateLimitStore(FixedKeyOptions);
        var middleware = RateLimitMiddleware.Create(store, FixedKeyOptions);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        var response = await middleware(
            context,
            (_, _) =>
            {
                var downstream = new HttpResponse { StatusCode = 200 };
                // A Limit header (which the middleware would otherwise write) plus a
                // non-Limit header, proving the guard is not a per-header check.
                downstream.Headers.Add("X-RateLimit-Limit", "7");
                downstream.Headers.Add("X-RateLimit-Remaining", "9");
                return ValueTask.FromResult(downstream);
            },
            CancellationToken.None
        );

        var limitValues = new List<string>();
        foreach (var value in response.Headers.GetValues("X-RateLimit-Limit"))
            limitValues.Add(value);
        var remainingValues = new List<string>();
        foreach (var value in response.Headers.GetValues("X-RateLimit-Remaining"))
            remainingValues.Add(value);
        var resetValues = new List<string>();
        foreach (var value in response.Headers.GetValues("X-RateLimit-Reset"))
            resetValues.Add(value);

        // Downstream values survive untouched (exact values, no duplicates)...
        await Assert.That(limitValues.Count).IsEqualTo(1);
        await Assert.That(limitValues[0]).IsEqualTo("7");
        await Assert.That(remainingValues.Count).IsEqualTo(1);
        await Assert.That(remainingValues[0]).IsEqualTo("9");
        // ...and the middleware wrote none of the group: no Reset at all, and the
        // would-be Limit/Remaining were not appended (their counts stay at 1).
        await Assert.That(resetValues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Rejected_Body_Keeps_The_Legacy_Shape()
    {
        // Characterization test for the extraction: the 429 body/headers must stay
        // byte-for-byte identical to the legacy middleware's. RefillRate=0 (fixed
        // window) keeps NextAvailableAt stable. The numeric Retry-After value and its
        // wall-clock formula are pinned separately by
        // Retry_After_Comes_From_The_Wall_Clock_Not_The_Store_Clock; here we pin the
        // wire shape and that the body carries the exact header value.
        var options = new RateLimitOptions
        {
            MaxTokens = 1,
            RefillRate = 0,
            RefillInterval = TimeSpan.FromSeconds(1),
            KeySelector = static _ => "test-key",
        };
        using var store = new InMemoryRateLimitStore(options);
        var middleware = RateLimitMiddleware.Create(store, options);

        var request = new HttpRequest { Method = "GET", Target = "/" };

        // Consume the only token.
        await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        // Next request is rate limited.
        var response = await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(429);
        var retryAfter = response.Headers["Retry-After"];
        await Assert.That(retryAfter).IsNotNull();
        await Assert.That(long.TryParse(retryAfter, out _)).IsTrue();
        await Assert.That(response.Headers["X-RateLimit-Limit"]).IsEqualTo("1");
        await Assert.That(response.Headers["Content-Type"]).IsEqualTo("application/json");
        await Assert
            .That(Encoding.UTF8.GetString(response.Body.Span))
            .IsEqualTo($$"""{"error":"rate-limited","retryAfter":{{retryAfter}}}""");
    }

    [Test]
    public async Task Retry_After_Comes_From_The_Wall_Clock_Not_The_Store_Clock()
    {
        // Spec §3.2.8 / legacy parity: RateLimitResponses.Rejected derives Retry-After
        // from DateTimeOffset.UtcNow, NOT from the store's internal TimeProvider. A store
        // clock far from the wall clock separates the two candidate numbers: the wall-clock
        // delta is huge, while the store-clock delta is the fixed-window constant
        // (31_536_000 s). RefillRate=0 keeps NextAvailableAt deterministic.
        var options = new RateLimitOptions
        {
            MaxTokens = 1,
            RefillRate = 0,
            RefillInterval = TimeSpan.FromSeconds(1),
            KeySelector = static _ => "test-key",
        };
        using var store = new InMemoryRateLimitStore(options)
        {
            TimeProvider = new StoreClock(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        };
        var middleware = RateLimitMiddleware.Create(store, options);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        var denied = await store.TryConsumeTokenAsync("test-key");
        await Assert.That(denied.Allowed).IsFalse();

        var storeClockDelta =
            denied.NextAvailableAt - store.TimeProvider.GetUtcNow().ToUnixTimeSeconds();

        // The store clock is fixed, so the wall clock is the only clock that can move.
        // Capture it around the call: Retry-After must be the wall-clock delta
        // max(NextAvailableAt - UtcNow, 1) evaluated at the instant the middleware reads
        // it, i.e. somewhere in [before, after].
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var expectedUpper = Math.Max(denied.NextAvailableAt - before, 1);
        var expectedLower = Math.Max(denied.NextAvailableAt - after, 1);

        await Assert.That(response.StatusCode).IsEqualTo(429);
        var retryAfter = response.Headers["Retry-After"];
        await Assert.That(retryAfter).IsNotNull();
        var retryAfterValue = long.Parse(retryAfter!);
        await Assert
            .That(retryAfterValue)
            .IsGreaterThanOrEqualTo(expectedLower)
            .Because("Retry-After must be the wall-clock delta, not the store-clock delta");
        await Assert
            .That(retryAfterValue)
            .IsLessThanOrEqualTo(expectedUpper)
            .Because("Retry-After must be the wall-clock delta, not the store-clock delta");
        await Assert
            .That(storeClockDelta)
            .IsEqualTo(31_536_000)
            .Because("the fixed-window store-clock delta is the constant the header must not use");
        await Assert
            .That(retryAfterValue)
            .IsNotEqualTo(storeClockDelta)
            .Because("emitting the store-clock delta would mean the test seam leaked onto the wire");
    }

    [Test]
    public async Task Retry_After_Clamps_To_One_When_NextAvailableAt_Is_In_The_Past()
    {
        // The Math.Max(..., 1) floor is only observable when NextAvailableAt is behind
        // the wall clock — impossible with a system-clock store (the fixed window always
        // yields now + 1 year). A store clock just after the Unix epoch puts
        // NextAvailableAt in 1971, so Retry-After must clamp to 1.
        var options = new RateLimitOptions
        {
            MaxTokens = 1,
            RefillRate = 0,
            RefillInterval = TimeSpan.FromSeconds(1),
            KeySelector = static _ => "test-key",
        };
        // 1970-01-02: non-zero so the store does not mistake the second access for
        // "first access, start full" (TryConsume's LastAccessTicks == 0 branch).
        using var store = new InMemoryRateLimitStore(options)
        {
            TimeProvider = new StoreClock(new DateTimeOffset(1970, 1, 2, 0, 0, 0, TimeSpan.Zero)),
        };
        var middleware = RateLimitMiddleware.Create(store, options);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        var denied = await store.TryConsumeTokenAsync("test-key");
        await Assert.That(denied.Allowed).IsFalse();
        await Assert
            .That(denied.NextAvailableAt)
            .IsLessThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Because("the fake store clock must place NextAvailableAt in the past");

        var response = await middleware(
            WebContext.Create(request),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(429);
        await Assert
            .That(response.Headers["Retry-After"])
            .IsEqualTo("1")
            .Because("a past NextAvailableAt must clamp to the 1-second floor");
        await Assert
            .That(Encoding.UTF8.GetString(response.Body.Span))
            .IsEqualTo("""{"error":"rate-limited","retryAfter":1}""");
    }

    /// <summary>
    /// Store clock for the Retry-After tests. The store reads
    /// <c>TimeProvider.GetUtcNow()</c>, so this must override it (unlike the
    /// keep-alive <see cref="ManualTimeProvider"/>, which only drives timers).
    /// The epoch is caller-chosen and non-zero, since <c>TryConsume</c> treats
    /// <c>LastAccessTicks == 0</c> as "first access, start full".
    /// </summary>
    private sealed class StoreClock(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class ThrowingRateLimitStore : IRateLimitStore
    {
        public ValueTask<RateLimitResult> TryConsumeTokenAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("store down");
    }
}
