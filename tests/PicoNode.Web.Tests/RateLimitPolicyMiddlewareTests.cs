namespace PicoNode.Web.Tests;

/// <summary>
/// Tasks 5/6 — the policy middleware's semantics (spec §3.2): the <c>ShouldApply</c>
/// gate, the per-tier <c>Applies</c>/<c>Key</c> classifiers, the ordered chain with
/// first-declared-wins state and headers, the two rejection paths (<c>LimitReached</c> and
/// <c>StoreError</c>, both reported through <c>OnRejected</c>), <c>FailOpen</c>, the key
/// sanitize rule, and the three passthrough paths.
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

    /// <summary>A policy builder whose per-tier stores all share one frozen <see cref="StoreClock"/>.</summary>
    private static RateLimitPolicyBuilder Policy(string name)
    {
        var builder = RateLimitPolicy.Create(name);
        builder.TimeProvider = new StoreClock();
        return builder;
    }

    private static ValueTask<HttpResponse> Submit(WebMiddleware middleware, string path) =>
        middleware(Context(path), Ok, CancellationToken.None);

    [Test]
    public async Task Chain_Reject_Reports_The_Rejecting_Tier_And_Keeps_The_First_Tier_Token_Spent()
    {
        var reported = new List<RateLimitRejection>();
        using var policy = Policy("web")
            .Tier("per-ip", RateLimitBudget.PerMinute(1, 1), static _ => "k")
            .Tier("per-user", RateLimitBudget.PerMinute(2, 2), static _ => "k")
            .OnRejected(reported.Add)
            .Build();
        var middleware = RateLimitMiddleware.Create(policy);

        // Exhaust the *second* tier's bucket directly, so the next request is allowed by
        // the first tier and rejected by the second — the ordered chain, in action.
        // PerMinute budgets throughout the chain tests: a per-second bucket refills from
        // the first call's timestamp (InMemoryRateLimitStore.cs:82-83), which would make
        // the multi-request assertions timing-dependent.
        await policy.Stores[1].TryConsumeTokenAsync("k");
        await policy.Stores[1].TryConsumeTokenAsync("k");

        var first = await Submit(middleware, "/api/data");
        await Assert.That(first.StatusCode).IsEqualTo(429);
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0].Policy).IsEqualTo("web");
        await Assert.That(reported[0].Tier).IsEqualTo("per-user");
        await Assert.That(reported[0].Reason).IsEqualTo(RateLimitRejectionReason.LimitReached);
        // The rejecting tier's *real* result travels with the rejection (Limit 2, not the
        // first tier's 1), and it drives the response headers.
        await Assert.That(reported[0].Result.Allowed).IsFalse();
        await Assert.That(reported[0].Result.Limit).IsEqualTo(2);
        await Assert.That(first.Headers["X-RateLimit-Limit"]).IsEqualTo("2");

        // No refund: the first tier's only token stayed spent when the later tier
        // rejected, so the second request is rejected by the *first* tier now.
        var second = await Submit(middleware, "/api/data");
        await Assert.That(second.StatusCode).IsEqualTo(429);
        await Assert.That(reported.Count).IsEqualTo(2);
        await Assert.That(reported[1].Tier).IsEqualTo("per-ip");
        await Assert.That(reported[1].Result.Limit).IsEqualTo(1);
    }

    [Test]
    public async Task First_Declared_Tier_Wins_The_Headers_And_State()
    {
        var reported = new List<RateLimitRejection>();
        using var policy = Policy("web")
            .Tier("coarse", RateLimitBudget.PerMinute(10, 10), static _ => "k")
            .Tier("fine", RateLimitBudget.PerMinute(2, 2), static _ => "k")
            .OnRejected(reported.Add)
            .Build();
        var middleware = RateLimitMiddleware.Create(policy);

        var context = Context("/api/data");
        var first = await middleware(context, Ok, CancellationToken.None);

        await Assert.That(first.StatusCode).IsEqualTo(200);
        await Assert.That(first.Headers["X-RateLimit-Limit"]).IsEqualTo("10");
        await Assert.That(first.Headers["X-RateLimit-Remaining"]).IsEqualTo("9");
        await Assert.That(first.Headers.TryGetValue("X-RateLimit-Reset", out _)).IsTrue();

        var state = context.Items[WebContextKeys.RateLimitState] as RateLimitState;
        await Assert.That(state).IsNotNull();
        await Assert.That(state!.Policy).IsEqualTo("web");
        await Assert.That(state.Tier).IsEqualTo("coarse");
        await Assert.That(state.Limit).IsEqualTo(10);
        await Assert.That(state.Remaining).IsEqualTo(9);

        // The second request still reports the first declared tier...
        var second = await Submit(middleware, "/api/data");
        await Assert.That(second.StatusCode).IsEqualTo(200);
        await Assert.That(second.Headers["X-RateLimit-Remaining"]).IsEqualTo("8");

        // ...while the second tier really was matched and consumed: its 2-token bucket is
        // empty now, so the third request is rejected by it (spec §3.2.4).
        var third = await Submit(middleware, "/api/data");
        await Assert.That(third.StatusCode).IsEqualTo(429);
        await Assert.That(third.Headers["X-RateLimit-Limit"]).IsEqualTo("2");
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0].Tier).IsEqualTo("fine");
    }

    [Test]
    public async Task Inner_Policy_429_Keeps_Its_Own_Headers()
    {
        using var outerPolicy = Policy("outer")
            .Tier("outer-tier", RateLimitBudget.PerMinute(100, 100), static _ => "k")
            .Build();
        using var innerPolicy = Policy("inner")
            .Tier("inner-tier", RateLimitBudget.PerMinute(1, 1), static _ => "k")
            .Build();
        var outer = RateLimitMiddleware.Create(outerPolicy);
        var inner = RateLimitMiddleware.Create(innerPolicy);

        // The outer tier has a winner on *both* requests, so only the group-level guard
        // (spec §3.2.5) can keep its Remaining/Reset off the inner policy's 429.
        var first = await outer(
            Context("/api/data"),
            (ctx, ct) => inner(ctx, Ok, ct),
            CancellationToken.None
        );
        await Assert.That(first.StatusCode).IsEqualTo(200);
        await Assert.That(first.Headers["X-RateLimit-Limit"]).IsEqualTo("1");

        var second = await outer(
            Context("/api/data"),
            (ctx, ct) => inner(ctx, Ok, ct),
            CancellationToken.None
        );

        await Assert.That(second.StatusCode).IsEqualTo(429);
        // The inner policy's own Limit is kept...
        await Assert.That(second.Headers["X-RateLimit-Limit"]).IsEqualTo("1");
        // ...and the outer policy's group never lands: no Remaining/Reset leak (they would
        // be 98/… from the outer tier), and no duplicate Limit either.
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Remaining", out _)).IsFalse();
        await Assert.That(second.Headers.TryGetValue("X-RateLimit-Reset", out _)).IsFalse();
        await Assert.That(second.Headers.TryGetValue("Retry-After", out _)).IsTrue();
        await Assert.That(second.Headers.GetValues("X-RateLimit-Limit").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task FailOpen_Skips_A_Throwing_Store_Without_Refunding_Earlier_Tokens()
    {
        var clock = new StoreClock();
        var tiers = new RateLimitTier[]
        {
            new("per-ip", RateLimitBudget.PerMinute(1, 1), static _ => "k", null),
            new("per-user", RateLimitBudget.PerMinute(1, 1), static _ => "k", null),
        };
        var stores = new InMemoryRateLimitStore[]
        {
            new(tiers[0].Budget) { TimeProvider = clock },
            new(tiers[1].Budget) { TimeProvider = clock },
        };
        stores[1].Dispose(); // the second tier's store is broken

        using var policy = new RateLimitPolicy("web", tiers, stores, true, null, null, null);
        var middleware = RateLimitMiddleware.Create(policy);

        var context = Context("/api/data");
        var first = await middleware(context, Ok, CancellationToken.None);
        await Assert.That(first.StatusCode).IsEqualTo(200);
        var state = context.Items[WebContextKeys.RateLimitState] as RateLimitState;
        await Assert.That(state).IsNotNull();
        await Assert.That(state!.Tier).IsEqualTo("per-ip");

        // No refund: the first tier's only token stayed spent while the broken tier was
        // skipped (spec §3.2.3), so the second request is a 429 from the first tier.
        var second = await Submit(middleware, "/api/data");
        await Assert.That(second.StatusCode).IsEqualTo(429);
        await Assert.That(second.Headers["X-RateLimit-Limit"]).IsEqualTo("1");
        await Assert.That(second.Headers.TryGetValue("Retry-After", out _)).IsTrue();
    }

    [Test]
    public async Task FailOpen_Set_From_The_Builder_Is_Honoured()
    {
        using var policy = Policy("web")
            .Tier("per-ip", RateLimitBudget.PerMinute(1, 1), static _ => "k")
            .Tier("per-user", RateLimitBudget.PerMinute(1, 1), static _ => "k")
            .FailOpen()
            .Build();
        var middleware = RateLimitMiddleware.Create(policy);

        await Assert.That(policy.FailOpen).IsTrue();

        // The host disposing a tier store early is the scenario: the default fail-closed
        // policy would 429 on the first request, the FailOpen() policy skips the broken
        // tier and passes the request through instead.
        policy.Stores[1].Dispose();

        var first = await Submit(middleware, "/api/data");
        await Assert.That(first.StatusCode).IsEqualTo(200);
        await Assert.That(first.Headers["X-RateLimit-Limit"]).IsEqualTo("1");

        // Same no-refund rule as the internal-ctor test: the live tier is now exhausted.
        var second = await Submit(middleware, "/api/data");
        await Assert.That(second.StatusCode).IsEqualTo(429);
        await Assert.That(second.Headers["X-RateLimit-Limit"]).IsEqualTo("1");
    }

    [Test]
    public async Task FailClosed_Throwing_Store_Returns_429_With_RetryAfter_60()
    {
        using var policy = Policy("web")
            .Tier("per-ip", RateLimitBudget.PerMinute(5, 5), static _ => "k")
            .Tier("per-user", RateLimitBudget.PerMinute(9, 9), static _ => "k")
            .Build();

        // Shield semantics (spec §3.2.6): store failures reject unless FailOpen() was set.
        await Assert.That(policy.FailOpen).IsFalse();
        policy.Stores[1].Dispose();
        var middleware = RateLimitMiddleware.Create(policy);

        var context = Context("/api/data");
        var response = await middleware(context, Ok, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(429);
        await Assert.That(response.Headers["Retry-After"]).IsEqualTo("60");
        // The failing tier's own MaxTokens, not the first tier's 5.
        await Assert.That(response.Headers["X-RateLimit-Limit"]).IsEqualTo("9");
        await Assert.That(response.Headers.TryGetValue("X-RateLimit-Remaining", out _)).IsFalse();
        await Assert
            .That(context.Items.ContainsKey(WebContextKeys.RateLimitState))
            .IsFalse()
            .Because("the rejected path must not report a rate-limit state");
    }

    [Test]
    public async Task OnRejected_Is_Called_Once_For_LimitReached_And_Once_For_StoreError()
    {
        var limitReached = new List<RateLimitRejection>();
        using var limited = Policy("limited")
            .Exempt("/api/health")
            .Tier("per-ip", RateLimitBudget.PerMinute(2, 2), static _ => "k")
            .OnRejected(limitReached.Add)
            .Build();
        var limitedMiddleware = RateLimitMiddleware.Create(limited);

        // Allow path: zero calls.
        await Assert.That((await Submit(limitedMiddleware, "/api/data")).StatusCode).IsEqualTo(200);
        await Assert.That(limitReached.Count).IsEqualTo(0);

        // Exempt path: zero calls and no token spent — the third request reaching 200 (it
        // has only 2 tokens) is what proves the exemption did not consume one.
        await Assert.That((await Submit(limitedMiddleware, "/api/health")).StatusCode).IsEqualTo(200);
        await Assert.That(limitReached.Count).IsEqualTo(0);
        await Assert.That((await Submit(limitedMiddleware, "/api/data")).StatusCode).IsEqualTo(200);
        await Assert.That(limitReached.Count).IsEqualTo(0);

        // LimitReached: exactly one call, with the rejecting tier's real result.
        var context = Context("/api/data");
        var rejected = await limitedMiddleware(context, Ok, CancellationToken.None);
        await Assert.That(rejected.StatusCode).IsEqualTo(429);
        await Assert.That(limitReached.Count).IsEqualTo(1);
        await Assert.That(limitReached[0].Policy).IsEqualTo("limited");
        await Assert.That(limitReached[0].Tier).IsEqualTo("per-ip");
        await Assert.That(limitReached[0].Reason).IsEqualTo(RateLimitRejectionReason.LimitReached);
        await Assert.That(limitReached[0].Result.Allowed).IsFalse();
        await Assert.That(limitReached[0].Result.Limit).IsEqualTo(2);
        await Assert.That(ReferenceEquals(limitReached[0].Context, context)).IsTrue();

        // StoreError: exactly one call, with the default (zero) result.
        var storeErrors = new List<RateLimitRejection>();
        using var broken = Policy("broken")
            .Tier("per-ip", RateLimitBudget.PerMinute(1, 1), static _ => "k")
            .OnRejected(storeErrors.Add)
            .Build();
        var brokenMiddleware = RateLimitMiddleware.Create(broken);
        broken.Stores[0].Dispose();

        var errorContext = Context("/api/data");
        var error = await brokenMiddleware(errorContext, Ok, CancellationToken.None);
        await Assert.That(error.StatusCode).IsEqualTo(429);
        await Assert.That(storeErrors.Count).IsEqualTo(1);
        await Assert.That(storeErrors[0].Policy).IsEqualTo("broken");
        await Assert.That(storeErrors[0].Tier).IsEqualTo("per-ip");
        await Assert.That(storeErrors[0].Reason).IsEqualTo(RateLimitRejectionReason.StoreError);
        await Assert.That(storeErrors[0].Result).IsEqualTo(default(RateLimitResult));
        await Assert.That(ReferenceEquals(storeErrors[0].Context, errorContext)).IsTrue();

        // One per request, never more: another failing request fires once more.
        await Assert.That((await Submit(brokenMiddleware, "/api/data")).StatusCode).IsEqualTo(429);
        await Assert.That(storeErrors.Count).IsEqualTo(2);

        // The LimitReached policy saw no traffic from the store-error half of the test.
        await Assert.That(limitReached.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Disposed_Policy_With_FailClosed_Returns_429()
    {
        var policy = Policy("web")
            .Tier("per-ip", RateLimitBudget.PerMinute(1, 1), static _ => "k")
            .Build();
        var middleware = RateLimitMiddleware.Create(policy);

        // Host-owned disposal (spec §3.1.1): the middleware outlives the policy's stores.
        policy.Dispose();

        var response = await Submit(middleware, "/api/data");
        await Assert.That(response.StatusCode).IsEqualTo(429);
        await Assert.That(response.Headers["Retry-After"]).IsEqualTo("60");
        await Assert.That(response.Headers["X-RateLimit-Limit"]).IsEqualTo("1");
    }

    [Test]
    public async Task Concurrent_Requests_Share_The_Tier_Bucket_Safely()
    {
        // PerMinute(1, 1), *not* PerSecond(1, 1): the store refills proportionally over
        // elapsed time from the first call's timestamp (InMemoryRateLimitStore.cs:82-83),
        // so a one-second window could hand a second token to a load-scheduling straggler
        // on a busy CI box, making "exactly 1 allowed" flaky. A one-minute window cannot
        // elapse inside the test, and the injected StoreClock is frozen anyway.
        using var policy = Policy("web")
            .Tier("per-ip", RateLimitBudget.PerMinute(1, 1), static _ => "k")
            .Build();
        var middleware = RateLimitMiddleware.Create(policy);

        const int requests = 64;
        var allowed = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[requests];

        for (var i = 0; i < requests; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await start.Task;
                var request = new HttpRequest
                {
                    Method = "GET",
                    Target = "/api/data",
                    Path = "/api/data",
                };
                var response = await middleware(
                    WebContext.Create(request),
                    Ok,
                    CancellationToken.None
                );
                if (response.StatusCode == 200)
                    Interlocked.Increment(ref allowed);
            });
        }

        start.SetResult();
        await Task.WhenAll(tasks);

        await Assert
            .That(allowed)
            .IsEqualTo(1)
            .Because("the shared tier bucket must admit exactly one of the parallel requests");
    }

    [Test]
    public async Task Sanitize_Truncates_At_256_And_Maps_Cr_Lf()
    {
        // The tier key *is* the request path, so the sanitize rule is the only thing that
        // can make two distinct paths share a bucket.
        using var policy = Policy("web")
            .Tier("per-ip", RateLimitBudget.PerMinute(1, 1), static ctx => ctx.Path)
            .Build();
        var middleware = RateLimitMiddleware.Create(policy);

        // Truncation to exactly 256 chars: everything past index 255 is dropped...
        var longKey = new string('a', 256) + new string('b', 44); // 300 chars
        var prefix = new string('a', 256); // its first 256 chars
        await Assert.That((await Submit(middleware, longKey)).StatusCode).IsEqualTo(200);
        await Assert
            .That((await Submit(middleware, prefix)).StatusCode)
            .IsEqualTo(429)
            .Because("the first 256 chars must address the same bucket as the 300-char key");

        // ...and *only* past 255: a key that differs at index 255 is a different bucket
        // (a shorter truncation would have collapsed it into the bucket above).
        await Assert
            .That((await Submit(middleware, new string('a', 255) + "b")).StatusCode)
            .IsEqualTo(200)
            .Because("a key differing at index 255 must not collide, so the limit is 256");

        // CR/LF are mapped to '_': the raw key and its mapped form share a bucket.
        await Assert.That((await Submit(middleware, "x\ry\nz")).StatusCode).IsEqualTo(200);
        await Assert
            .That((await Submit(middleware, "x_y_z")).StatusCode)
            .IsEqualTo(429)
            .Because("CR and LF must map to '_' before the store sees the key");
    }
}
