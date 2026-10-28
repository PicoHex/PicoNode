namespace PicoJsonRpc.Tests;

public sealed class LifecycleTests
{
    // Resolved per USE, not in a static initializer: a missing fixture must fail the
    // test that wanted it, with NodeFixture's "this is a broken copy rule" message -
    // not blow up every test in the class as a TypeInitializationException, and not
    // touch Acquire_UnknownRuntime_Throws, which needs no fixture at all.
    private static string FixtureJs => NodeFixture.PathFor(NodeFixture.Echo);

    private static string ChattyJs => NodeFixture.PathFor(NodeFixture.Chatty);

    [Test]
    [SkipUnlessNode]
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
    [SkipUnlessNode]
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
    [SkipUnlessNode]
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
    [SkipUnlessNode]
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
    [SkipUnlessNode]
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

    /// <summary>
    /// T7.3: the crash counter must outlive the channel it counts. A crash
    /// destroys its channel, so a counter kept ON the channel dies with the
    /// generation it measured — every respawn then starts from 0, the threshold
    /// is unreachable in the real flow, and a child that dies on start-up is
    /// respawned on every single request (no backoff, no cap, no circuit-open).
    /// <para>
    /// Second half: the <see cref="TimeoutException"/> must not destroy the state
    /// it was decided from. Reading the deadline off the channel that acquire had
    /// just detached made the refusal itself the reason the next acquire resumed.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Second_acquire_after_crash_still_respects_backoff()
    {
        var lc = new ProcessLifecycle(
            "echo",
            ["node", FixtureJs],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        // exactly the start-up-crash flow: spawn → die → respawn, three times,
        // each crash on a DIFFERENT channel
        for (var crash = 1; crash <= ProcessLifecycle.BackoffCrashThreshold; crash++)
        {
            var ch = await lc.AcquireAsync(default);
            Kill(ch);
            lc.Churn(ch); // what the client's read loop does on the child's EOF
            await Assert.That(ch.ConsecutiveCrashes).IsEqualTo(crash);
        }

        // 4th acquire: backoff is armed, so it refuses instead of spawning yet
        // another doomed child (a per-channel counter reads 0 here and spawns)
        await Assert.That(async () => await lc.AcquireAsync(default)).Throws<TimeoutException>();
        // 5th acquire: the refusal must leave the state behind it intact — the
        // old order nulled _channel and then read the deadline off it, so this
        // acquire skipped the backoff and spawned immediately
        await Assert.That(async () => await lc.AcquireAsync(default)).Throws<TimeoutException>();
        await lc.RecycleAsync();
    }

    /// <summary>
    /// T7.1: a child that writes ≫ one pipe buffer to stderr BEFORE it serves
    /// its first request must still complete the round trip quickly. Without a
    /// reader on stderr the child parks inside write(2) — the call never
    /// returns, with no error and no diagnostic.
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task Chatty_child_does_not_deadlock()
    {
        var lc = new ProcessLifecycle(
            "chatty",
            ["node", ChattyJs],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        await using var client = new JsonRpcClient(lc);
        try
        {
            // The budget is the assertion: a genuinely blocked child never answers, so any finite
            // deadline catches the deadlock this test exists for - the number only decides how
            // quickly the failure is reported. It was 5 s; the Windows CI runners (win-x64 and
            // win-arm64, 2 cores, the suite's classes in parallel) needed longer for node to start,
            // push 200 KB through the pipe and get the answer back, because the drain continuation
            // waits for a thread-pool thread. 15 s keeps a real deadlock detectable and stops the
            // suite from reddening merely because the runner is busy. The structural fix - a
            // dedicated drain thread that never competes with the pool - is recorded as a known
            // limitation instead of being smuggled in with a timeout change.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ch = await lc.AcquireAsync(cts.Token);
            var resp = await client.CallAsync("ping", "{}", cts.Token);
            await Assert.That(resp.Error).IsNull();

            // the tail keeps the LAST bytes of 200 KB and stays bounded
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && !ch.StderrTail.EndsWith("CHATTY-STDERR-END\n"))
                await Task.Delay(20);
            await Assert.That(ch.StderrTail).EndsWith("CHATTY-STDERR-END\n");
            await Assert.That(ch.StderrTail.Length).IsLessThanOrEqualTo(8 * 1024);
        }
        finally
        {
            await lc.RecycleAsync();
        }
    }

    /// <summary>
    /// T7.1's failure path, made permanent (T7.6c): a child that writes to stderr
    /// and exits NON-ZERO must surface those last words IN THE ERROR THE CALLER
    /// SEES.
    /// <para>
    /// A dead child answers nothing, so the stderr tail is the only account of why
    /// it died; an <see cref="IOException"/> saying "closed the stream" and nothing
    /// else is undebuggable. The assertion is deliberately NOT "it threw": it
    /// throws with the marker <b>in the message</b>, which is the half a
    /// drain-without-report implementation would pass and this would catch.
    /// </para>
    /// <para>
    /// Reached through <see cref="NodeFixture.StartAsync(string)"/>, so the fixture
    /// is copy-rule-checked and the exit is OUT of process (the child is the one
    /// exiting non-zero, not a simulated EOF). The fixture writes its marker with a
    /// blocking <c>write(2)</c> before <c>process.exit(3)</c>, so the bytes are
    /// already in the pipe when stdout EOFs — no child-side race can lose them.
    /// </para>
    /// <para>
    /// <b>Why this test runs alone.</b> The bytes being in the pipe is only half of
    /// the path: <see cref="JsonRpcClient"/> reads them through
    /// <c>CaptureStderrTail(250 ms)</c>, which waits on the channel's stderr DRAIN
    /// task — and that drain is an async <c>Read</c> whose continuation needs a
    /// thread-pool thread. Under a busy pool the continuation can be scheduled
    /// after the 250 ms budget expires, and the capture then returns the empty
    /// window even though the child's last words were written before it exited.
    /// That is exactly how this test used to flake (observed: the message was
    /// <c>The pipe is being closed.</c> with NO tail, while the suite was spawning
    /// the <c>pwsh</c>/<c>git</c> children of <c>ReleaseGuardHolesTests</c> and
    /// <c>WireReleaseScriptTests</c>). No constant fixes that — a bigger budget is
    /// the same bet against a scheduler — so the load COUPLING is what is removed:
    /// <see cref="NotInParallelAttribute"/> here (no constraint key) makes this test
    /// the only test running in its process, which is the strongest form of
    /// "serialise the heavy release-script tests against the tail test".
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessNode]
    [NotInParallel]
    public async Task Dead_child_stderr_tail_reaches_the_caller()
    {
        await using var client = await NodeFixture.StartAsync(NodeFixture.Crash);
        var ex = await Assert
            .That(async () => await client.CallAsync("ping", "{}", CancellationToken.None))
            .Throws<IOException>();

        var message = ex!.Message;
        // the marker AND the exit-reason line: the tail arrives whole, not clipped to
        // its first byte, and not emptied by the drain losing a race with the exit.
        await Assert
            .That(message)
            .Contains("DEAD-CHILD-STDERR-MARKER-7c41e0")
            .Because(
                "the caller's IOException must carry the child's stderr tail — a dead "
                    + "child's last words are the only account of why it died. Actual "
                    + "message: "
                    + message
            );
        await Assert.That(message).Contains("failed on purpose (exit 3)");
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
