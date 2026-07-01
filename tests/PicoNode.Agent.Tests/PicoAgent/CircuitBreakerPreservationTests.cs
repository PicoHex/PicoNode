using PicoNode.AI;

namespace PicoNode.Agent.Tests.PicoAgentReloadTests;

/// <summary>
/// Design-review flaw #4 (P1): ReloadProviderConfigs unconditionally re-creates
/// every ICircuitBreaker on hot-reload, wiping out Open state and effectively
/// disabling failover protection until failures reoccur.
///
/// Fix contract: MergeBreakers preserves the existing ICircuitBreaker instance
/// for any provider name that survives a reload, and only instantiates a fresh
/// CircuitBreaker for genuinely new provider names. Providers that were removed
/// from the new config are dropped.
/// </summary>
public class CircuitBreakerPreservationTests
{
    [Test]
    public async Task MergeBreakers_PreservesExistingBreakerForSameProviderName()
    {
        var oldBreaker = new CircuitBreaker();
        for (var i = 0; i < 10; i++)
            oldBreaker.RecordFailure();

        var previous = new Dictionary<string, ICircuitBreaker> { ["p1"] = oldBreaker };

        var merged = global::PicoAgent.Agent.MergeBreakers(previous, new[] { "p1" });

        await Assert.That(merged.ContainsKey("p1")).IsTrue();
        await Assert.That(ReferenceEquals(merged["p1"], oldBreaker)).IsTrue();
    }

    [Test]
    public async Task MergeBreakers_CreatesNewBreakerForNewProviderName()
    {
        var previous = new Dictionary<string, ICircuitBreaker>();

        var merged = global::PicoAgent.Agent.MergeBreakers(previous, new[] { "brand_new" });

        await Assert.That(merged.ContainsKey("brand_new")).IsTrue();
        await Assert.That(merged["brand_new"]).IsNotNull();
    }

    [Test]
    public async Task MergeBreakers_DropsProviderRemovedFromNewConfig()
    {
        var previous = new Dictionary<string, ICircuitBreaker>
        {
            ["removed"] = new CircuitBreaker(),
            ["kept"] = new CircuitBreaker(),
        };

        var merged = global::PicoAgent.Agent.MergeBreakers(previous, new[] { "kept" });

        await Assert.That(merged.ContainsKey("kept")).IsTrue();
        await Assert.That(merged.ContainsKey("removed")).IsFalse();
    }

    [Test]
    public async Task MergeBreakers_MixedAddAndKeep_HandlesBothCorrectly()
    {
        var oldKept = new CircuitBreaker();
        var previous = new Dictionary<string, ICircuitBreaker> { ["kept"] = oldKept };

        var merged = global::PicoAgent.Agent.MergeBreakers(previous, new[] { "kept", "added" });

        await Assert.That(ReferenceEquals(merged["kept"], oldKept)).IsTrue();
        await Assert.That(merged.ContainsKey("added")).IsTrue();
        await Assert.That(ReferenceEquals(merged["added"], oldKept)).IsFalse();
    }
}
