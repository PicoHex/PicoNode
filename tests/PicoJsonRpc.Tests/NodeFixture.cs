using System.Diagnostics;

namespace PicoJsonRpc.Tests;

/// <summary>
/// The ONE place a JSON-RPC test gets a child process (T7.6). Before this, two
/// classes each rolled their own node probe (<c>ClientTests.NodeRuntime</c>,
/// <c>LifecycleTests.NodePresent</c>) and neither checked the fixture files at
/// all — so a machine with no <c>node</c> and a broken
/// <c>CopyToOutputDirectory</c> rule both ended as the same quiet green run.
/// <para>
/// <b>The contract — a missing precondition is never a quiet skip:</b>
/// <list type="number">
/// <item><b>Missing fixture = FAILURE.</b> <see cref="StartAsync(string)"/> and
/// <see cref="PathFor"/> throw <see cref="FileNotFoundException"/> before the node
/// probe is consulted, so "the fixture was not copied to the output directory" can
/// never be reported as "no node on this machine". A broken copy rule is a broken
/// build, not an environment fact.</item>
/// <item><b>No <c>node</c> = a LOUD skip.</b> Every test that needs a real child
/// carries <see cref="SkipUnlessNodeAttribute"/>, whose reason names what a
/// node-less run loses (see <see cref="SkipUnlessNodeAttribute.NodeLessRunReason"/>).
/// Reaching the helper without the guard is an
/// <see cref="InvalidOperationException"/> — it fails, it does not quietly do
/// nothing.</item>
/// <item><b>The copy rule is checked on every run</b>, node or no node:
/// <see cref="Named"/> lists the fixtures the suite names, and
/// <c>NodeFixtureTests</c> asserts each one is in the output directory (and that
/// the output directory holds no unnamed fixture, in any language).</item>
/// </list>
/// </para>
/// <para>
/// <b>Exactly what a node-less run loses — 21 of 59, measured</b> (a PATH with no
/// <c>node</c>: 38 passed, 21 skipped, 0 failed):
/// <list type="bullet">
/// <item><see cref="LifecycleTests"/> — <b>8 of 9</b>: <c>Acquire_SpawndNode_AndRecycle_Kills</c>,
/// <c>Acquire_Concurrent_ReusesSingleInstance</c>, <c>Acquire_ThreeCrashes_EntersBackoff</c>,
/// <c>Acquire_SuccessResetsBackoff</c>, <c>Churn_StaleChannel_DoesNotCorruptCurrent</c>,
/// <c>Second_acquire_after_crash_still_respects_backoff</c>,
/// <c>Chatty_child_does_not_deadlock</c>, <c>Dead_child_stderr_tail_reaches_the_caller</c>.
/// Each one spawns, kills or recycles a real
/// child, so there is nothing to observe without a runtime. The 9th,
/// <c>Acquire_UnknownRuntime_Throws</c>, asserts a MISSING runtime's error and
/// therefore still runs — it is the only test that is meaningful precisely because
/// something cannot be started.</item>
/// <item><see cref="ClientTests"/> — <b>12 of 13</b>: every test that builds
/// <c>TestChannel</c>. Those channels script their OWN pipes, but they still carry a
/// real <see cref="Process"/> handle (<see cref="JsonRpcChannel"/> requires one), so
/// they need a real child. The 13th,
/// <c>Route_flags_a_dropped_frame_only_when_a_subscription_could_have_had_it</c>, is
/// pure registry bookkeeping and still runs.</item>
/// <item><see cref="NodeFixtureTests"/> — <b>1 of 6</b>:
/// <c>StartAsync_round_trips_a_call_through_the_echo_fixture</c>, the helper's own
/// success path (it spawns a child, so it cannot run either). The other five
/// cover the loud contract and the copy rule, and they must run precisely when
/// node is missing — a contract test that skips along with the thing it checks is
/// the hole this task closes.</item>
/// <item><see cref="EnvelopeTests"/>, <see cref="FramerTests"/>,
/// <see cref="WireConformanceTests"/> — none: no child, no fixture.</item>
/// </list>
/// A skip is acceptable only if it says all of that out loud — which is why the
/// skip reason below repeats the count instead of saying "no node".
/// </para>
/// </summary>
internal static class NodeFixture
{
    /// <summary>The NDJSON echo fixture — one response per request frame.</summary>
    public const string Echo = "echo_server.mjs";

    /// <summary>Writes ≫ one pipe buffer to stderr before answering (T7.1).</summary>
    public const string Chatty = "chatty_server.mjs";

    /// <summary>
    /// Writes two lines to stderr, then exits <b>3</b> before reading any request
    /// (T7.6c). The failure path's fixture: nothing can be answered, so the child's
    /// stderr tail is the whole of what the caller can learn.
    /// </summary>
    public const string Crash = "crash_server.mjs";

    /// <summary>
    /// The golden WIRE SNAPSHOT (T7.7): the exact bytes the encoder emits for the
    /// <c>WireSnapshotTests</c> corpus. Not a runtime fixture — no child is started —
    /// but it is copied by the same <c>Fixtures/**</c> rule, so it belongs in
    /// <see cref="Named"/> or the reverse copy-rule check would fail on it.
    /// </summary>
    public const string WireSnapshot = "wire-v1.ndjson";

    /// <summary>
    /// Every fixture a test in this assembly names. The copy-rule test iterates
    /// this list, so adding a fixture here is what gets it copy-checked; its
    /// reverse check makes the list and the output directory agree both ways — in
    /// ANY language, so a fixture nobody names cannot sit in the output unnoticed.
    /// <para>
    /// <c>echo_server.py</c> used to sit beside these: copied by the same
    /// <c>Fixtures/**</c> glob, started by nobody. T7.6b deleted it rather than
    /// wiring it. It was a byte-for-byte SHAPE duplicate of
    /// <see cref="Echo"/> (same <c>{jsonrpc,id,result}</c> frame, same
    /// string-embedded <c>{"echo": &lt;request&gt;}</c> payload), so it proved nothing
    /// about the wire that the node fixture does not; its only differences were
    /// incidental (json.dumps' <c>" ",</c> separators, and <c>ensure_ascii</c>
    /// escaping a CJK reply where node echoes raw UTF-8) — neither of which our
    /// reader is sensitive to (it parses JSON, not bytes), and the escaped form is
    /// already pinned by <c>Unpaired_surrogate_in_caller_text_is_substituted_not_thrown</c>.
    /// Keeping it would have cost a second runtime (and its own probe, its own
    /// skip reason, and a <c>python3</c>-vs-<c>python</c> divergence on Windows,
    /// where <c>python3</c> is a Store stub) to test the same bytes twice.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Named = [Echo, Chatty, Crash, WireSnapshot];

    /// <summary>
    /// The node probe, evaluated at most once per test run: a skip decision is
    /// made per test at registration, and 19 probes per run would be 19 child
    /// processes for one boolean.
    /// </summary>
    private static readonly Lazy<bool> NodeProbe = new(ProbeNode);

    /// <summary>
    /// Is there a <c>node</c> runtime to spawn the fixtures with?
    /// <para>
    /// A 3 s timeout, not a bare <c>WaitForExit()</c>: a <c>node</c> shim that hangs
    /// (a broken version manager, a corporate wrapper) must not park test
    /// registration. A timeout, a non-zero exit or a launch failure all mean
    /// ABSENT — the skip that produces is loud and counted, so a false negative
    /// costs coverage, never a false green.
    /// </para>
    /// </summary>
    public static bool NodeAvailable() => NodeProbe.Value;

    /// <summary>
    /// Start a real, live <see cref="JsonRpcClient"/> over a fixture, for the
    /// tests that want an honest round trip instead of a scripted channel.
    /// <para>
    /// Order is the contract: the fixture is resolved FIRST (missing → failure),
    /// the node probe second (absent → <see cref="InvalidOperationException"/>).
    /// Disposing the returned client recycles the child
    /// (<see cref="JsonRpcClient.DisposeAsync"/> closes stdin and asks the
    /// lifecycle to recycle), so <c>await using</c> is the whole teardown.
    /// </para>
    /// </summary>
    public static Task<JsonRpcClient> StartAsync(string fixture) =>
        StartAsync(fixture, NodeAvailable);

    /// <summary>
    /// The probe-injectable form — the seam that pins the ORDER above: even with
    /// <paramref name="nodePresent"/> reporting <c>false</c>, a missing fixture
    /// must still report itself as missing.
    /// </summary>
    internal static Task<JsonRpcClient> StartAsync(string fixture, Func<bool> nodePresent)
    {
        // resolve the fixture BEFORE anything looks at the machine: an uncopied
        // fixture is our bug and must never be diluted into "no node here"
        var path = PathFor(fixture);
        if (!nodePresent())
        {
            throw new InvalidOperationException(
                $"fixture '{fixture}' is present at '{path}' but the 'node' runtime is not on "
                    + "PATH, so it cannot be started. Guard the test with [SkipUnlessNode] - a "
                    + "test that reaches this line has no guard, and skipping is only acceptable "
                    + "when it is declared (see NodeFixture for what a node-less run loses)."
            );
        }
        var lifecycle = new ProcessLifecycle(
            Path.GetFileNameWithoutExtension(fixture),
            ["node", path],
            idleTimeout: TimeSpan.FromMinutes(1)
        );
        return Task.FromResult(new JsonRpcClient(lifecycle));
    }

    /// <summary>
    /// The fixture's path in the OUTPUT directory, or a
    /// <see cref="FileNotFoundException"/> that says what to fix. It throws rather
    /// than returning a path that does not exist: an uncopied fixture handed to
    /// <c>node</c> surfaces far away as a child that exits immediately, which is
    /// the confusing failure T7.6 exists to replace.
    /// </summary>
    public static string PathFor(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"test fixture '{fixture}' was not found at '{path}'. This is a BROKEN COPY "
                    + "RULE, not a missing runtime: PicoJsonRpc.Tests.csproj must copy "
                    + "'Fixtures\\**' to the output directory (None Update with "
                    + "CopyToOutputDirectory=\"PreserveNewest\"). Fix the build - do not skip.",
                path
            );
        }
        return path;
    }

    private static bool ProbeNode()
    {
        try
        {
            using var probe = Process.Start(
                new ProcessStartInfo("node", "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            );
            if (probe is null)
                return false;
            // Read the version first: `node --version` writes one short line, so
            // this cannot fill the pipe and cannot deadlock against the 3 s wait.
            var version = probe.StandardOutput.ReadToEnd();
            if (!probe.WaitForExit(TimeSpan.FromSeconds(3)))
            {
                try
                {
                    probe.Kill(entireProcessTree: true);
                }
                catch
                {
                    // the hung probe is best-effort disposable
                }
                return false;
            }
            return probe.ExitCode == 0 && version.Trim().Length > 0;
        }
        catch
        {
            return false; // Win32Exception: no `node` on PATH at all
        }
    }
}

/// <summary>
/// <c>[SkipUnlessNode]</c> — the executable form of
/// <see cref="NodeFixture.NodeAvailable"/>, for any test that needs a real child.
/// <para>
/// A plain <c>Skip.Unless(NodeFixture.NodeAvailable(), "no node")</c> at the top
/// of every such test is what the suite had (twice, with two hand-rolled probes
/// and no fixture check). This attribute replaces it so the guard cannot be
/// forgotten silently, the probe happens once, and the reason is the SAME loud
/// sentence everywhere — naming the classes and the count the run loses instead
/// of the useless "node is not installed".
/// </para>
/// </summary>
internal sealed class SkipUnlessNodeAttribute() : SkipAttribute(NodeLessRunReason)
{
    /// <summary>
    /// The loud reason. It deliberately repeats the loss count: a node-less run
    /// must be readable evidence that 21 tests did not run, not a footnote.
    /// </summary>
    public const string NodeLessRunReason =
        "SKIPPED - no 'node' runtime on PATH. This run loses 21 node-dependent tests: "
        + "LifecycleTests 8/9 (spawn, recycle, crash-backoff, chatty stderr, the dead "
        + "child's stderr tail), "
        + "ClientTests 12/13 (scripted channels still need a real process handle) and "
        + "NodeFixtureTests 1/6 (the helper's round trip). Fixture/copy-rule and wire tests "
        + "still ran. Install node to recover the coverage; a missing FIXTURE is a "
        + "different, loud failure and never skips.";

    /// <summary>
    /// Skip exactly when the runtime is absent. The probe is cached, so this is a
    /// property read per test rather than a process per test.
    /// </summary>
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!NodeFixture.NodeAvailable());

    /// <summary>Put the reason AND the test name into the skip line, so a node-less
    /// run names what it skipped rather than merely counting it.</summary>
    protected override string GetSkipReason(TestRegisteredContext context) =>
        $"{NodeLessRunReason} Test: {context.TestName}.";
}
