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
    /// EVERY language, not <c>*.mjs</c>: the <c>Fixtures/**</c> glob copies whatever
    /// is in the directory, so a language-scoped filter is a hole a new fixture can
    /// fall through. It used to be <c>*.mjs</c> so that <c>echo_server.py</c> — copied
    /// by the glob but started by nobody — would not fail the run; that file is now
    /// deleted (T7.6b), so the check covers the whole directory and the next unnamed
    /// fixture is caught whichever runtime it targets.
    /// </para>
    /// <para>
    /// EVERY DIRECTORY, not the top level: the glob is <c>Fixtures/**</c>, which is
    /// recursive, so <c>Fixtures/sub/x.mjs</c> is copied into the output directory —
    /// and a top-level enumeration never sees it, leaving it copied, unnamed and
    /// ungoverned. The rule and the copy rule now cover the same tree.
    /// </para>
    /// </summary>
    [Test]
    public async Task No_unnamed_fixture_is_copied_to_the_output_directory()
    {
        var fixtures = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var copied = NodeFixture.CopiedFixtures(fixtures);

        // Non-vacuity: an unresolved/missing output directory would make the check
        // below trivially true for an empty set.
        await Assert
            .That(copied)
            .IsNotEmpty()
            .Because($"the copy rule must have put fixtures at '{fixtures}'");

        var unnamed = copied.Except(NodeFixture.Named, StringComparer.Ordinal).ToArray();
        await Assert
            .That(unnamed)
            .IsEmpty()
            .Because(
                "every fixture in the output directory must be listed in NodeFixture.Named so "
                    + "the copy rule is asserted for it, in any language and at any depth; add it there."
            );
    }

    /// <summary>
    /// The proof that the reverse check is DEPTH-scoped, not merely language-scoped:
    /// a fixture one directory down — the shape the recursive <c>Fixtures/**</c> glob
    /// copies — must be reported as unnamed. It runs in the test assembly's OWN
    /// scratch space, NOT in the real <c>Fixtures/</c>: a committed nested fixture
    /// would either have to be named (and then it is governed, which is a different
    /// claim) or fail every run by design.
    /// <para>
    /// The scratch root is under <see cref="AppContext.BaseDirectory"/> rather than
    /// <see cref="Path.GetTempPath()"/> on purpose: when TMP/TEMP are unset,
    /// <c>GetTempPath()</c> falls back to <c>C:\WINDOWS\</c> and the test dies of
    /// <see cref="UnauthorizedAccessException"/> — an environment artifact, not the
    /// property under test. The output directory is writable by definition (the run
    /// already wrote its fixtures there), so this measures the walk and nothing else.
    /// </para>
    /// </summary>
    [Test]
    public async Task The_reverse_check_sees_a_fixture_in_a_subdirectory()
    {
        var scratch = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "fixture-depth-scratch-" + Guid.NewGuid().ToString("N")[..12]
        );
        var nestedDir = System.IO.Path.Combine(scratch, "sub");
        try
        {
            Directory.CreateDirectory(nestedDir);
            await File.WriteAllTextAsync(System.IO.Path.Combine(scratch, NodeFixture.Echo), "// top");
            await File.WriteAllTextAsync(System.IO.Path.Combine(nestedDir, "nested.mjs"), "// deep");

            var copied = NodeFixture.CopiedFixtures(scratch);

            await Assert
                .That(copied)
                .IsEquivalentTo([NodeFixture.Echo, "sub/nested.mjs"])
                .Because("the walk must report a nested fixture, not only the top level");

            var unnamed = copied.Except(NodeFixture.Named, StringComparer.Ordinal).ToArray();
            await Assert
                .That(unnamed)
                .IsEquivalentTo(["sub/nested.mjs"])
                .Because(
                    "a fixture copied by the recursive glob one directory down must be caught "
                        + "as unnamed; a top-level walk would silently ignore it"
                );
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
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
