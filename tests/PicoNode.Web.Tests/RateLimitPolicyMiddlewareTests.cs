namespace PicoNode.Web.Tests;

/// <summary>
/// Task 5 — the policy middleware's single-tier semantics (spec §3.2): the
/// <c>ShouldApply</c> gate, the per-tier <c>Applies</c>/<c>Key</c> classifiers, the store
/// consumption, the state/header write order and the two passthrough paths.
/// </summary>
public sealed class RateLimitPolicyMiddlewareTests
{
    /// <summary>
    /// Clock for the per-tier stores. Must override <c>GetUtcNow</c> (the store reads it;
    /// the keep-alive <see cref="ManualTimeProvider"/> only drives timers) and its epoch
    /// must be non-zero, since <c>TryConsume</c> treats <c>LastAccessTicks == 0</c> as a
    /// first access and starts the bucket full.
    /// </summary>
    private sealed class StoreClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static WebContext Context(string path) =>
        WebContext.Create(new HttpRequest { Method = "GET", Target = path, Path = path });

    private static ValueTask<HttpResponse> Ok(WebContext context, CancellationToken ct) =>
        ValueTask.FromResult(new HttpResponse { StatusCode = 200 });

    [Test]
    public async Task Single_Tier_Allow_Writes_Headers_And_State()
    {
        var builder = RateLimitPolicy.Create("web")
            .Tier("per-ip", RateLimitBudget.PerSecond(3, 1), static _ => "k");
        builder.TimeProvider = new StoreClock();
        using var policy = builder.Build();
        var middleware = RateLimitMiddleware.Create(policy);

        var context = Context("/api/data");
        var stateVisibleDownstream = false;
        var response = await middleware(
            context,
            (ctx, _) =>
            {
                // State must be injected before `await next`, so downstream can read it.
                stateVisibleDownstream = ctx.Items.ContainsKey(WebContextKeys.RateLimitState);
                return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
            },
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers.TryGetValue("X-RateLimit-Limit", out var limit)).IsTrue();
        await Assert.That(limit).IsEqualTo("3");
        await Assert
            .That(response.Headers.TryGetValue("X-RateLimit-Remaining", out var remaining))
            .IsTrue();
        await Assert.That(remaining).IsEqualTo("2");
        await Assert.That(response.Headers.TryGetValue("X-RateLimit-Reset", out _)).IsTrue();

        var state = context.Items[WebContextKeys.RateLimitState] as RateLimitState;
        await Assert.That(state).IsNotNull();
        await Assert.That(state!.Policy).IsEqualTo("web");
        await Assert.That(state.Tier).IsEqualTo("per-ip");
        await Assert.That(state.Limit).IsEqualTo(3);
        await Assert.That(state.Remaining).IsEqualTo(2);
        await Assert
            .That(stateVisibleDownstream)
            .IsTrue()
            .Because("the state must be written before await next, not after");
    }

    [Test]
    public async Task Single_Tier_Reject_Returns_429_And_No_State()
    {
        var builder = RateLimitPolicy.Create("web")
            .Tier("per-ip", RateLimitBudget.PerSecond(1, 1), static _ => "k");
        builder.TimeProvider = new StoreClock();
        using var policy = builder.Build();
        var middleware = RateLimitMiddleware.Create(policy);

        var request = new HttpRequest { Method = "GET", Target = "/api/data", Path = "/api/data" };

        // First request spends the only token.
        var allowed = await middleware(WebContext.Create(request), Ok, CancellationToken.None);
        await Assert.That(allowed.StatusCode).IsEqualTo(200);

        var context = WebContext.Create(request);
        var nextCalls = 0;
        var response = await middleware(
            context,
            (_, _) =>
            {
                nextCalls++;
                return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
            },
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(429);
        await Assert.That(response.Headers.TryGetValue("Retry-After", out _)).IsTrue();
        await Assert
            .That(context.Items.ContainsKey(WebContextKeys.RateLimitState))
            .IsFalse()
            .Because("the rejected path must not report a rate-limit state");
        await Assert
            .That(nextCalls)
            .IsEqualTo(0)
            .Because("a rejection returns before invoking the downstream handler");
    }

    [Test]
    public async Task No_Matching_Tier_Passes_Through_Untouched()
    {
        var builder = RateLimitPolicy.Create("web")
            .Tier("per-ip", RateLimitBudget.PerSecond(1, 1), static _ => null);
        builder.TimeProvider = new StoreClock();
        using var policy = builder.Build();
        var middleware = RateLimitMiddleware.Create(policy);

        var request = new HttpRequest { Method = "GET", Target = "/api/data", Path = "/api/data" };
        var expected = new HttpResponse { StatusCode = 200 };
        var nextCalls = 0;
        WebRequestHandler next = (_, _) =>
        {
            nextCalls++;
            return ValueTask.FromResult(expected);
        };

        // Twice: a null key means "tier not applicable" (no bucket, hence no second-request
        // 429). A fabricated fallback key would spend the single token and 429 here.
        var first = await middleware(WebContext.Create(request), next, CancellationToken.None);
        var context = WebContext.Create(request);
        var second = await middleware(context, next, CancellationToken.None);

        await Assert.That(first.StatusCode).IsEqualTo(200);
        await Assert.That(second.StatusCode).IsEqualTo(200);
        await Assert.That(ReferenceEquals(second, expected)).IsTrue();
        await Assert.That(nextCalls).IsEqualTo(2);
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Limit", out _)).IsFalse();
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Remaining", out _)).IsFalse();
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Reset", out _)).IsFalse();
        await Assert.That(context.Items.ContainsKey(WebContextKeys.RateLimitState)).IsFalse();

        // The skipped tier's bucket is untouched: its single token is still available.
        var untouched = await policy.Stores[0].TryConsumeTokenAsync("k");
        await Assert.That(untouched.Allowed).IsTrue();
    }

    [Test]
    public async Task Tier_Applies_False_Skips_The_Tier()
    {
        var builder = RateLimitPolicy.Create("web")
            .Tier(
                "per-ip",
                RateLimitBudget.PerSecond(1, 1),
                static _ => "k",
                static _ => false
            );
        builder.TimeProvider = new StoreClock();
        using var policy = builder.Build();
        var middleware = RateLimitMiddleware.Create(policy);

        var request = new HttpRequest { Method = "GET", Target = "/api/data", Path = "/api/data" };
        var nextCalls = 0;
        WebRequestHandler next = (_, _) =>
        {
            nextCalls++;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        // Twice: if the tier were evaluated despite Applies=false, the first request
        // would spend the only token and the second would be a 429.
        var first = await middleware(WebContext.Create(request), next, CancellationToken.None);
        var context = WebContext.Create(request);
        var second = await middleware(context, next, CancellationToken.None);

        await Assert.That(first.StatusCode).IsEqualTo(200);
        await Assert.That(second.StatusCode).IsEqualTo(200);
        await Assert.That(nextCalls).IsEqualTo(2);
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Limit", out _)).IsFalse();
        await Assert.That(context.Items.ContainsKey(WebContextKeys.RateLimitState)).IsFalse();
        await Assert.That((await policy.Stores[0].TryConsumeTokenAsync("k")).Allowed).IsTrue();
    }

    [Test]
    public async Task Policy_Exemption_Passes_Through_Untouched()
    {
        var builder = RateLimitPolicy.Create("web")
            .Exempt("/api/health")
            .Tier("per-ip", RateLimitBudget.PerSecond(1, 1), static _ => "k");
        builder.TimeProvider = new StoreClock();
        using var policy = builder.Build();
        var middleware = RateLimitMiddleware.Create(policy);

        var nextCalls = 0;
        WebRequestHandler next = (_, _) =>
        {
            nextCalls++;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var exemptRequest = new HttpRequest
        {
            Method = "GET",
            Target = "/api/health",
            Path = "/api/health",
        };

        // Twice: if the exemption were ignored, the first request would spend the only
        // token and the second would be a 429 (the control below proves the tier bites).
        var first = await middleware(WebContext.Create(exemptRequest), next, CancellationToken.None);
        var context = WebContext.Create(exemptRequest);
        var second = await middleware(context, next, CancellationToken.None);

        await Assert.That(first.StatusCode).IsEqualTo(200);
        await Assert.That(second.StatusCode).IsEqualTo(200);
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Limit", out _)).IsFalse();
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Remaining", out _)).IsFalse();
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Reset", out _)).IsFalse();
        await Assert.That(context.Items.ContainsKey(WebContextKeys.RateLimitState)).IsFalse();

        // Control: the same middleware does limit a non-exempt path, so the passthrough
        // above proves the exemption and not a policy that never limits anything.
        var limitedRequest = new HttpRequest
        {
            Method = "GET",
            Target = "/api/data",
            Path = "/api/data",
        };
        var controlAllowed = await middleware(
            WebContext.Create(limitedRequest),
            next,
            CancellationToken.None
        );
        var limited = await middleware(WebContext.Create(limitedRequest), next, CancellationToken.None);
        await Assert.That(controlAllowed.StatusCode).IsEqualTo(200);
        await Assert.That(limited.StatusCode).IsEqualTo(429);
        // Two exempt passthroughs plus the control's allowed request; the 429 never
        // reaches next.
        await Assert.That(nextCalls).IsEqualTo(3);
    }
}
