namespace PicoNode.Web.Tests;

public sealed class RateLimitBudgetTests
{
    [Test]
    public async Task PerSecond_Builds_The_Budget()
    {
        var b = RateLimitBudget.PerSecond(300, 30);
        await Assert.That(b.MaxTokens).IsEqualTo(300);
        await Assert.That(b.RefillRate).IsEqualTo(30);
        await Assert.That(b.RefillInterval).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    private sealed class StoreClock : TimeProvider // NOT the keep-alive ManualTimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // non-zero epoch, see below

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Test]
    public async Task Store_From_Budget_Enforces_It()
    {
        // The store reads TimeProvider.GetUtcNow() (InMemoryRateLimitStore.cs:54), so the clock must
        // override GetUtcNow — ManualTimeProvider overrides only GetTimestamp/CreateTimer/
        // TimestampFrequency (it drives keep-alive timers), never GetUtcNow. Its epoch must be
        // non-zero: TryConsume treats LastAccessTicks == 0 as "first access, start full" (:75),
        // which would allow every call on a clock starting at 0.
        var clock = new StoreClock();
        using var store = new InMemoryRateLimitStore(RateLimitBudget.PerSecond(1, 1))
        {
            TimeProvider = clock,
        };
        var first = await store.TryConsumeTokenAsync("k", CancellationToken.None);
        var second = await store.TryConsumeTokenAsync("k", CancellationToken.None);
        await Assert.That(first.Allowed).IsTrue();
        await Assert.That(second.Allowed).IsFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await store.TryConsumeTokenAsync("k", CancellationToken.None)).Allowed).IsTrue();
    }
}
