namespace PicoNode.Web.Tests;

public sealed class RateLimitPolicyBuilderTests
{
    /// <summary>
    /// Clock for policy tests. Must override <c>GetUtcNow</c>: the store reads it, while
    /// the keep-alive <see cref="ManualTimeProvider"/> only drives timers. The epoch is
    /// non-zero because <c>TryConsume</c> treats <c>LastAccessTicks == 0</c> as a first
    /// access and starts the bucket full.
    /// </summary>
    private sealed class StoreClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static WebContext Context(string path) =>
        WebContext.Create(new HttpRequest { Method = "GET", Target = path, Path = path });

    private static RateLimitPolicyBuilder Builder(string name = "web") =>
        RateLimitPolicy.Create(name);

    private static RateLimitPolicyBuilder OneTier(
        RateLimitBudget budget,
        string name = "web",
        string tierName = "anonymous"
    ) => Builder(name).Tier(tierName, budget, static _ => "k");

    [Test]
    public async Task Zero_Tiers_Throws()
    {
        await Assert
            .That(() => Builder().Build())
            .Throws<InvalidOperationException>()
            .Because("a policy with no tiers would silently never limit anything");
    }

    [Test]
    public async Task Duplicate_Tier_Names_Throw()
    {
        // Tier names feed observation and RateLimitRejection.Tier, so a duplicate
        // would make the rejecting tier ambiguous.
        var duplicate = Builder()
            .Tier("anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Tier("anonymous", RateLimitBudget.PerSecond(2, 2), static _ => "k");

        await Assert.That(() => duplicate.Build()).Throws<InvalidOperationException>();

        // Comparison is Ordinal: names differing only by case are distinct tiers.
        using var distinct = Builder()
            .Tier("Anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Tier("anonymous", RateLimitBudget.PerSecond(2, 2), static _ => "k")
            .Build();

        await Assert.That(distinct.Tiers.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Invalid_Budget_Throws()
    {
        // default(RateLimitBudget) is the shape a caller gets from an uninitialized
        // field: MaxTokens=0 and RefillInterval=TimeSpan.Zero. It cannot be hidden by
        // an internal ctor, so Build() must reject it (spec §3.1).
        var uninitialized = default(RateLimitBudget);
        await Assert
            .That(() => OneTier(uninitialized).Build())
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(() => OneTier(new RateLimitBudget(0, 1, TimeSpan.FromSeconds(1))).Build())
            .Throws<ArgumentOutOfRangeException>()
            .Because("MaxTokens must be positive");

        await Assert
            .That(() => OneTier(new RateLimitBudget(5, 1, TimeSpan.Zero)).Build())
            .Throws<ArgumentOutOfRangeException>()
            .Because("RefillInterval must be positive");
    }

    [Test]
    public async Task RefillRate_Zero_Is_Legal()
    {
        // RefillRate=0 is a fixed window, not an error (spec §3.4). A negative rate
        // behaves like 0, so it is legal too.
        var clock = new StoreClock();
        var builder = OneTier(RateLimitBudget.PerMinute(1, 0), tierName: "fixed")
            .Tier("negative", new RateLimitBudget(1, -1, TimeSpan.FromMinutes(1)), static _ => "n");
        builder.TimeProvider = clock;

        using var policy = builder.Build();

        await Assert.That(policy.Tiers[0].Budget.RefillRate).IsEqualTo(0);
        await Assert.That(policy.Tiers[1].Budget.RefillRate).IsEqualTo(-1);

        await Assert.That((await policy.Stores[0].TryConsumeTokenAsync("k")).Allowed).IsTrue();
        await Assert.That((await policy.Stores[0].TryConsumeTokenAsync("k")).Allowed).IsFalse();

        clock.Advance(TimeSpan.FromHours(2));
        await Assert
            .That((await policy.Stores[0].TryConsumeTokenAsync("k")).Allowed)
            .IsFalse()
            .Because("a zero refill rate must never replenish the bucket");
    }

    [Test]
    public async Task Exempt_Accumulates_As_Or_Whitelist()
    {
        using var policy = Builder()
            .Exempt("/api/health")
            .Exempt("/api/ready")
            .Tier("anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Build();

        await Assert.That(policy.IsExempt(Context("/api/health"))).IsTrue();
        await Assert
            .That(policy.IsExempt(Context("/api/ready")))
            .IsTrue()
            .Because("repeated Exempt(...) calls accumulate into an OR whitelist");
        await Assert.That(policy.IsExempt(Context("/api/other"))).IsFalse();

        // No Exempt(...) call at all => the compiled whitelist is null, so nothing is exempt.
        using var bare = OneTier(RateLimitBudget.PerSecond(1, 1), name: "bare").Build();
        await Assert.That(bare.IsExempt(Context("/api/health"))).IsFalse();

        await Assert.That(() => Builder().Exempt(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Applies_Ands_With_The_Exempt_Whitelist()
    {
        using var policy = Builder()
            .Exempt("/api/health")
            .Applies(static _ => true)
            .Tier("anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Build();

        await Assert.That(policy.Applies).IsNotNull();
        await Assert.That(policy.ShouldApply(Context("/api/data"))).IsTrue();
        await Assert
            .That(policy.ShouldApply(Context("/api/health")))
            .IsFalse()
            .Because("ShouldApply = (Applies is null || Applies(ctx)) && !IsExempt(ctx) — AND, not OR");

        using var rejecting = Builder("rejecting")
            .Applies(static _ => false)
            .Tier("anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Build();
        await Assert.That(rejecting.ShouldApply(Context("/api/data"))).IsFalse();

        using var open = OneTier(RateLimitBudget.PerSecond(1, 1), name: "open").Build();
        await Assert.That(open.Applies).IsNull();
        await Assert.That(open.ShouldApply(Context("/api/data"))).IsTrue();

        // Null configuration arguments are rejected at build time so they cannot
        // become a request-time NullReferenceException.
        await Assert
            .That(() => Builder().Tier(null!, RateLimitBudget.PerSecond(1, 1), static _ => "k"))
            .Throws<ArgumentNullException>();
        await Assert
            .That(() => Builder().Tier("t", RateLimitBudget.PerSecond(1, 1), null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => Builder().Applies(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => Builder().OnRejected(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Exempt_Does_Not_Exempt_A_Longer_Segment()
    {
        // The longer-segment rule needs a policy whose only prefix is /api/health: if
        // /api were also exempt, /api/healthz would (correctly) match /api and mask it.
        using var health = Builder("health")
            .Exempt("/api/health")
            .Tier("anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Build();

        await Assert.That(health.IsExempt(Context("/api/health"))).IsTrue();
        await Assert.That(health.IsExempt(Context("/api/health/checks"))).IsTrue();
        await Assert
            .That(health.IsExempt(Context("/api/healthz")))
            .IsFalse()
            .Because("/api/health must not exempt the longer segment /api/healthz (spec §4)");
        await Assert.That(health.IsExempt(Context("/api/healthy"))).IsFalse();

        // Segment boundary: /api matches /api and /api/x, but neither /apix nor /api2.
        using var api = Builder("api")
            .Exempt("/api")
            .Tier("anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Build();

        await Assert.That(api.IsExempt(Context("/api"))).IsTrue();
        await Assert.That(api.IsExempt(Context("/api/x"))).IsTrue();
        await Assert
            .That(api.IsExempt(Context("/apix")))
            .IsFalse()
            .Because("the prefix must end at a segment boundary");
        await Assert.That(api.IsExempt(Context("/api2"))).IsFalse();
        await Assert
            .That(api.IsExempt(Context("/api/healthz")))
            .IsTrue()
            .Because("/api is a whole-segment prefix of /api/healthz, by design");

        // A prefix ending in '/' is a boundary by itself: it needs no second boundary.
        using var trailing = Builder("trailing")
            .Exempt("/api/")
            .Tier("anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Build();
        await Assert.That(trailing.IsExempt(Context("/api/x"))).IsTrue();
        await Assert.That(trailing.IsExempt(Context("/api"))).IsFalse();
        await Assert.That(trailing.IsExempt(Context("/apix"))).IsFalse();

        // Matching is a literal Ordinal span comparison: no dot-segment normalization
        // and no percent decoding (spec §3.4).
        await Assert.That(api.IsExempt(Context("/api/../admin"))).IsTrue();
        await Assert.That(api.IsExempt(Context("/%61pi/x"))).IsFalse();
        await Assert.That(api.IsExempt(Context("/API/x"))).IsFalse();

        // The empty prefix follows the literal rule: it matches any rooted path.
        using var all = Builder("all")
            .Exempt("")
            .Tier("anonymous", RateLimitBudget.PerSecond(1, 1), static _ => "k")
            .Build();
        await Assert.That(all.IsExempt(Context("/anything"))).IsTrue();
        await Assert.That(all.IsExempt(Context("/"))).IsTrue();
    }

    [Test]
    public async Task Tiers_Are_ReadOnly_And_Ordered()
    {
        var builder = Builder()
            .Tier("first", RateLimitBudget.PerSecond(1, 1), static _ => "1")
            .Tier("second", RateLimitBudget.PerSecond(2, 2), static _ => "2");

        using var policy = builder.Build();

        // Declaration order is preserved, and the list is a frozen copy: adding a tier
        // after Build() must not be visible to the already-built policy.
        builder.Tier("third", RateLimitBudget.PerSecond(3, 3), static _ => "3");

        await Assert.That(policy.Tiers.Count).IsEqualTo(2);
        await Assert.That(policy.Tiers[0].Name).IsEqualTo("first");
        await Assert.That(policy.Tiers[1].Name).IsEqualTo("second");
        await Assert.That(policy.Stores.Length).IsEqualTo(policy.Tiers.Count);
    }

    [Test]
    public async Task Builder_TimeProvider_Reaches_Every_Tier_Store()
    {
        var clock = new StoreClock();
        var builder = Builder()
            .Tier("trusted", RateLimitBudget.PerSecond(300, 30), static _ => "t")
            .Tier("anonymous", RateLimitBudget.PerSecond(60, 1), static _ => "a")
            .Tier("probe", RateLimitBudget.PerMinute(10, 1), static _ => "p");
        builder.TimeProvider = clock;

        using var policy = builder.Build();

        await Assert.That(policy.Stores.Length).IsEqualTo(3);
        for (var i = 0; i < policy.Stores.Length; i++)
        {
            await Assert
                .That(ReferenceEquals(policy.Stores[i].TimeProvider, clock))
                .IsTrue()
                .Because($"tier {i}'s store must use the injected clock, not TimeProvider.System");
        }
    }
}
