namespace PicoJsonRpc.Tests;

/// <summary>
/// The loud contract of <see cref="NodeFixture"/> — the two preconditions a
/// JSON-RPC child test has are checked here, and NONE of these tests may skip,
/// because the thing under test is "what happens when a precondition is missing":
/// <list type="number">
/// <item>a fixture that is not in the output directory is a <b>failure</b> (a
/// broken <c>CopyToOutputDirectory</c> rule), never a skip — and it is reported
/// <b>before</b> the node probe is consulted, so "no fixture" can never be
/// laundered into "no node";</item>
/// <item>every fixture the suite names is actually copied to the output
/// directory, and the output directory holds no fixture nobody names — otherwise
/// a broken copy rule would surface as a confusing child that exits immediately
/// inside whichever node test happened to run.</item>
/// </list>
/// </summary>
public sealed class NodeFixtureTests
{
    private const string MissingFixture = "no-such-fixture-pico-xyz.mjs";

    /// <summary>
    /// A fixture name that does not exist must THROW. Before T7.6 there was no
    /// fixture check anywhere: a missing file reached the child as a bad path, and
    /// the only thing that ever skipped was "node is not installed" — so a broken
    /// copy rule and a node-less machine produced the same green run.
    /// </summary>
    [Test]
    public async Task Missing_fixture_fails_loudly_instead_of_skipping()
    {
        await Assert
            .That(async () => await NodeFixture.StartAsync(MissingFixture))
            .Throws<FileNotFoundException>()
            .WithMessageContaining(MissingFixture);
    }

    /// <summary>
    /// The anti-conflation pin, and the reason <c>StartAsync</c> takes the node
    /// probe as an argument: with node reported ABSENT, a missing fixture must still
    /// be a <see cref="FileNotFoundException"/>. If the node check ever moves
    /// first, this test reports an <see cref="InvalidOperationException"/> instead —
    /// i.e. exactly the conflation the contract forbids.
    /// </summary>
    [Test]
    public async Task Missing_fixture_is_reported_before_the_node_check()
    {
        await Assert
            .That(async () =>
                await NodeFixture.StartAsync(MissingFixture, static () => false)
            )
            .Throws<FileNotFoundException>()
            .WithMessageContaining(MissingFixture);
    }

    /// <summary>
    /// The other side of the same contract: a PRESENT fixture with no node is a
    /// loud <see cref="InvalidOperationException"/> naming the runtime and the
    /// guard, not a silent <c>null</c> or a skip. Tests are kept out of this state
    /// by <see cref="SkipUnlessNodeAttribute"/>, so this is the backstop for a fresh
    /// test that forgot the guard — it fails, it cannot quietly do nothing.
    /// </summary>
    [Test]
    public async Task Absent_node_runtime_is_a_failure_not_a_quiet_skip()
    {
        await Assert
            .That(async () => await NodeFixture.StartAsync(NodeFixture.Echo, static () => false))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("node");
    }

    /// <summary>
    /// The csproj copy rule, asserted where it can actually be seen: the output
    /// directory of the running test assembly.
    /// <para>
    /// Node-INDEPENDENT on purpose — this must be checked on a machine with no node
    /// too, because that is precisely where a broken copy rule would hide behind
    /// every node test's skip.
    /// </para>
    /// </summary>
    [Test]
    public async Task Every_named_fixture_is_copied_to_the_output_directory()
    {
        foreach (var fixture in NodeFixture.Named)
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
            await Assert
                .That(File.Exists(path))
                .IsTrue()
                .Because(
                    $"fixture '{fixture}' must be copied to the test output directory by "
                        + "PicoJsonRpc.Tests.csproj; expected it at " + path
                );
        }
    }

    /// <summary>
    /// The reverse direction: a fixture that IS in the output directory but is not in
    /// <see cref="NodeFixture.Named"/> is a fixture the copy-rule test does not
    /// cover, so the list would silently stop describing the suite. Failing here is
    /// what keeps "add a fixture" and "get it checked" the same action.
    /// <para>
    /// Scoped to <c>*.mjs</c> on purpose: <c>echo_server.py</c> is copied by the same
    /// glob but is never started by any test, and settling it (wire it up or delete
    /// it) is T7.6b's decision, not this task's. A future fixture in ANY language that
    /// a test names belongs in <see cref="NodeFixture.Named"/> regardless.
    /// </para>
    /// </summary>
    [Test]
    public async Task No_unnamed_fixture_is_copied_to_the_output_directory()
    {
        var fixtures = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var copied = Directory.Exists(fixtures)
            ? Directory.GetFiles(fixtures, "*.mjs").Select(System.IO.Path.GetFileName)
            : [];
        var unnamed = copied.Except(NodeFixture.Named).ToArray();
        await Assert
            .That(unnamed)
            .IsEmpty()
            .Because(
                "every '.mjs' fixture in the output directory must be listed in "
                    + "NodeFixture.Named so the copy rule is asserted for it; add it there."
            );
    }

    /// <summary>
    /// The helper's SUCCESS path — without it <see cref="NodeFixture.StartAsync(string)"/>
    /// is only ever exercised by its three throw cases, so a helper that never
    /// actually starts anything would pass this class. It is the smallest honest
    /// proof: the fixture runs, a request round-trips, and <c>await using</c> tears
    /// the child down.
    /// </summary>
    [Test]
    [SkipUnlessNode]
    public async Task StartAsync_round_trips_a_call_through_the_echo_fixture()
    {
        await using var client = await NodeFixture.StartAsync(NodeFixture.Echo);
        var response = await client.CallAsync(
            "echo",
            "{\"text\":\"hello\"}",
            CancellationToken.None
        );
        await Assert.That(response.Error).IsNull();
        // the fixture echoes the whole request inside `result` as a JSON string
        await Assert.That(response.Result).Contains("hello");
    }
}
