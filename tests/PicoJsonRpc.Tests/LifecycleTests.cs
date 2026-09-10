namespace PicoJsonRpc.Tests;

public sealed class LifecycleTests
{
    private static readonly string FixtureJs = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "echo_server.mjs"
    );

    [Test]
    public async Task Acquire_SpawndNode_AndRecycle_Kills()
    {
        var lc = new ProcessLifecycle(
            "echo",
            ["node", FixtureJs],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        var ch = await lc.AcquireAsync(default);
        await Assert.That(ch.Process.HasExited).IsFalse();
        await Assert.That(ch.Name).IsEqualTo("echo");
        var pid1 = ch.Process.Id;
        await lc.RecycleAsync();
        await Assert.That(ch.RecycleRequested).IsTrue();
        // the recycled instance is gone — the next acquire spawns a fresh one
        var ch2 = await lc.AcquireAsync(default);
        await Assert.That(ch2.Process.Id).IsNotEqualTo(pid1);
        await lc.RecycleAsync();
    }

    [Test]
    public async Task Acquire_Concurrent_ReusesSingleInstance()
    {
        var lc = new ProcessLifecycle(
            "echo",
            ["node", FixtureJs],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        var ch1 = await lc.AcquireAsync(default);
        var ch2 = await lc.AcquireAsync(default);
        await Assert.That(ch2).IsSameReferenceAs(ch1);
        await lc.RecycleAsync();
    }

    [Test]
    public async Task Acquire_ThreeCrashes_EntersBackoff()
    {
        var lc = new ProcessLifecycle(
            "echo",
            ["node", FixtureJs],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        var ch = await lc.AcquireAsync(default);
        Kill(ch);
        lc.Churn(ch);
        lc.Churn(ch);
        lc.Churn(ch); // 3 consecutive crashes → backoff armed
        await Assert.That(ch.ConsecutiveCrashes).IsEqualTo(3);
        await Assert.That(ch.NextRetryAtUtc).IsGreaterThan(DateTime.UtcNow);
        await Assert.That(async () => await lc.AcquireAsync(default)).Throws<TimeoutException>();
        await lc.RecycleAsync();
    }

    [Test]
    public async Task Acquire_SuccessResetsBackoff()
    {
        var lc = new ProcessLifecycle(
            "echo",
            ["node", FixtureJs],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        var ch = await lc.AcquireAsync(default);
        Kill(ch);
        lc.Churn(ch);
        lc.Churn(ch);
        lc.Churn(ch);
        await Assert.That(async () => await lc.AcquireAsync(default)).Throws<TimeoutException>();
        // a healthy call (MarkHealthy) resets the backoff state
        lc.MarkHealthy();
        var ch2 = await lc.AcquireAsync(default);
        await Assert.That(ch2.ConsecutiveCrashes).IsEqualTo(0);
        await lc.RecycleAsync();
    }

    /// <summary>Simulate an unplanned child crash: hard-kill + settle so
    /// Process.HasExited is true before the lifecycle observes it.</summary>
    private static void Kill(JsonRpcChannel ch)
    {
        if (!ch.Process.HasExited)
            ch.Process.Kill(entireProcessTree: true);
        ch.Process.WaitForExit(2000);
    }

    [Test]
    public async Task Churn_StaleChannel_DoesNotCorruptCurrent()
    {
        // regression-lock for the review #4 guard: a stale-reader EOF must not
        // attribute a crash to (or arm backoff on) the CURRENT channel.
        var lc = new ProcessLifecycle(
            "echo",
            ["node", FixtureJs],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        var ch1 = await lc.AcquireAsync(default);
        await lc.RecycleAsync(); // ch1 dies; lifecycle drops it
        var ch2 = await lc.AcquireAsync(default); // current channel
        lc.Churn(ch1); // stale reader EOF for the recycled channel
        await Assert.That(ch2.ConsecutiveCrashes).IsEqualTo(0);
        await Assert.That(ch2.NextRetryAtUtc).IsEqualTo(default);
        var ch3 = await lc.AcquireAsync(default); // no backoff armed
        await Assert.That(ch3).IsSameReferenceAs(ch2);
        await lc.RecycleAsync();
    }

    [Test]
    public async Task Acquire_UnknownRuntime_Throws()
    {
        var lc = new ProcessLifecycle(
            "bogus",
            ["definitely-not-a-runtime-picoagent-xyz"],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        await Assert
            .That(async () => await lc.AcquireAsync(default))
            .Throws<InvalidOperationException>();
    }
}
