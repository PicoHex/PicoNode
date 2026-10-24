using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PicoJsonRpc.Tests;

/// <summary>
/// The release half of the wire guard, asserted on what <c>scripts/release.ps1</c>
/// PRINTS — by running it, not by grepping its source (T7.7, fix round 1).
/// <para>
/// <b>Why the string match had to go.</b> The first version of this test asserted
/// that the script's TEXT contains <c>Get-FileHash</c>, <c>wire hash</c>,
/// <c>wire-change:</c> and the snapshot path. All four also live in the script's
/// header comment, so deleting the entire decision + warning block left the test
/// green: it proved a comment mentioned hashing, not that anything hashed,
/// compared or printed. A test that cannot fail on the defect it names is worse
/// than no test — it is a false claim of coverage, and this repository has been
/// bitten by exactly that before (<c>CiWorkflowTests</c> found a release workflow
/// that ran no tests while two docs said it did).
/// </para>
/// <para>
/// <b>What it does instead.</b> <see cref="ReleaseScriptProbe"/> builds a throwaway
/// repository — a copy of the REAL script, one fake packable project, a stubbed
/// <c>dotnet</c> that reports an unchanged API and a fixed next version, and the
/// real <c>git</c> — then runs <c>release.ps1 -DryRun</c> against it four times and
/// compares the emitted <c>== Decision</c> block, line for line, including the
/// warning notes:
/// <list type="number">
/// <item>the snapshot is not in the tag → <c>absent</c>, with the UNMEASURABLE note;</item>
/// <item>the snapshot equals the tag's blob → <c>unchanged</c>, and no note at all;</item>
/// <item>the worktree snapshot moved → <c>changed</c>, with the <c>wire-change:</c> /
/// lockstep note. This is the verdict the obligation hangs on and it had NO coverage;</item>
/// <item>no snapshot on disk → the field is gone and the wire is reported UNPINNED.</item>
/// </list>
/// That walks every branch of <c>Test-WireSnapshotChangedSince</c> (<c>absent</c> /
/// <c>changed</c> / <c>unchanged</c>) plus the <c>Get-WireHash</c> null path, and it
/// asserts the printed sha256 is the sha256 of the file — so the number the release
/// dry run shows is the number <see cref="WireSnapshotTests"/> prints in a failure.
/// </para>
/// <para>
/// Delete the decision block and this test fails with "the script printed no
/// '== Decision' section"; move a note or change a word in the obligation and it
/// fails with the expected-vs-actual segment. See the T7.7 report, fix round 1.
/// </para>
/// </summary>
public sealed class WireReleaseScriptTests
{
    /// <summary>
    /// The four verdicts, from the script's own output. <c>absent</c> first: it is
    /// what the very next release will print (the snapshot post-dates
    /// <c>v2026.5.1</c>), and it reports "unmeasurable" rather than a comfortable
    /// "unchanged".
    /// </summary>
    [Test]
    [SkipUnlessReleaseScriptHost]
    public async Task Release_script_prints_the_wire_verdict_and_names_the_obligation_when_the_wire_moved()
    {
        using var probe = ReleaseScriptProbe.Create();

        // -- (1) the snapshot is not in the tag: UNMEASURABLE, not "unchanged" ----
        var absent = probe.Run();
        await Assert.That(absent.ExitCode).IsEqualTo(0).Because(absent.Explain);
        await Assert
            .That(string.IsNullOrWhiteSpace(absent.StandardError))
            .IsTrue()
            .Because("a run that prints a warning to stderr must not pass as a clean one: " + absent.Explain);
        await Assert
            .That(absent.Decision())
            .IsEqualTo(
                Normalise(
                    $"""
                       last release : {ReleaseScriptProbe.LastTag}
                       API changed  : false
                       wire hash    : {probe.SnapshotHash()}
                       wire changed : absent (vs {ReleaseScriptProbe.LastTag})
                       next version : v{ReleaseScriptProbe.StubNextVersion}
                       the wire was not pinned at {ReleaseScriptProbe.LastTag} (the snapshot post-dates it), so any shape change
                         between {ReleaseScriptProbe.LastTag} and here is UNMEASURABLE: check the commits since that tag for a
                         'wire-change:' line - the JSON-RPC 2.0 conformance change is exactly such a commit,
                         and 'API changed : false' does not describe it. From the next release on, this
                         comparison is exact.
                    """
                )
            )
            .Because(
                "the first release after the snapshot lands must say the comparison is "
                    + "unmeasurable, not that the wire did not move"
            );

        // -- (2) the snapshot is in the tag and unchanged: no note, just the field --
        probe.Retag(ReleaseScriptProbe.LastTag);

        var unchanged = probe.Run();
        await Assert.That(unchanged.ExitCode).IsEqualTo(0).Because(unchanged.Explain);
        await Assert
            .That(unchanged.Decision())
            .IsEqualTo(
                Normalise(
                    $"""
                       last release : {ReleaseScriptProbe.LastTag}
                       API changed  : false
                       wire hash    : {probe.SnapshotHash()}
                       wire changed : unchanged (vs {ReleaseScriptProbe.LastTag})
                       next version : v{ReleaseScriptProbe.StubNextVersion}
                    """
                )
            )
            .Because(
                "an unchanged snapshot must print 'unchanged' and stay silent - a note "
                    + "here is noise that trains the operator to ignore the field"
            );

        // -- (3) the worktree snapshot moved: the obligation, out loud ------------
        var unchangedHash = probe.SnapshotHash();
        probe.MoveSnapshot();
        var changedHash = probe.SnapshotHash();
        await Assert
            .That(changedHash)
            .IsNotEqualTo(unchangedHash)
            .Because("sanity: moving the snapshot must change the digest the script prints");

        var changed = probe.Run();
        await Assert.That(changed.ExitCode).IsEqualTo(0).Because(changed.Explain);
        await Assert
            .That(changed.Decision())
            .IsEqualTo(
                Normalise(
                    $"""
                       last release : {ReleaseScriptProbe.LastTag}
                       API changed  : false
                       wire hash    : {changedHash}
                       wire changed : changed (vs {ReleaseScriptProbe.LastTag})
                       next version : v{ReleaseScriptProbe.StubNextVersion}
                       the wire shape MOVED since {ReleaseScriptProbe.LastTag}: 'API changed : false' does not cover it
                         the commit that carried the new bytes must have a line starting with 'wire-change:'
                         describing them, and any consumer pinned to the old shape (PicoAgent.Binding)
                         must ship in lockstep. Announce it in the FEATURE commit: 'chore(release):' is a
                         baseline refresh and cannot carry the description.
                    """
                )
            )
            .Because(
                "this is the whole point of the release half: when the bytes moved, the "
                    + "release says so and names the wire-change: / lockstep obligation"
            );

        // -- (4) no snapshot at all: the wire is UNPINNED, loudly -----------------
        probe.DeleteSnapshot();

        var unpinned = probe.Run();
        await Assert.That(unpinned.ExitCode).IsEqualTo(0).Because(unpinned.Explain);
        await Assert
            .That(unpinned.Decision())
            .IsEqualTo(
                Normalise(
                    $"""
                       last release : {ReleaseScriptProbe.LastTag}
                       API changed  : false
                       wire hash    : (no snapshot at {WireSnapshotTests.SnapshotRelativePath}) - the wire is UNPINNED
                       next version : v{ReleaseScriptProbe.StubNextVersion}
                    """
                )
            )
            .Because(
                "a deleted snapshot must not silently report a verdict; it reports that "
                    + "nothing is pinned"
            );
    }

    /// <summary>
    /// The wire hash is measured BEFORE the version is asked for (fix round 1).
    /// <para>
    /// A number printed after the decision it is meant to inform cannot inform it, and
    /// while the hash was computed after <c>next-version</c> the ordering could not be
    /// fixed later without re-testing the whole block. This pins the order behaviourally
    /// rather than textually: the stubbed <c>dotnet</c> REWRITES the snapshot at the
    /// moment it answers <c>next-version</c>, so the hash the script prints is the
    /// <b>before</b> digest. If the computation moved back below the version call, the
    /// printed hash would be the rewritten file's digest and this fails.
    /// </para>
    /// </summary>
    [Test]
    [SkipUnlessReleaseScriptHost]
    public async Task Wire_hash_is_measured_before_the_version_is_asked_for()
    {
        using var probe = ReleaseScriptProbe.Create();
        var before = probe.SnapshotHash();
        var rewritten = probe.WriteReplacementSnapshotForSwap(
            "{\"jsonrpc\":\"2.0\",\"method\":\"rewritten-during-next-version\"}\n"
        );

        var run = probe.Run(
            new Dictionary<string, string>
            {
                ["WIRE_PROBE_SWAP"] = probe.SnapshotPath,
                ["WIRE_PROBE_CONTENT"] = rewritten,
            }
        );

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Explain);
        await Assert
            .That(probe.SnapshotHash())
            .IsNotEqualTo(before)
            .Because(
                "the stub must have actually rewritten the snapshot when next-version was called, "
                    + "otherwise this test proves nothing about the order: "
                    + run.Explain
            );
        await Assert
            .That(run.Decision())
            .Contains($"wire hash    : {before}")
            .Because(
                "the printed hash must be the digest measured BEFORE the version was asked for; "
                    + "a hash taken afterwards would be the rewritten file's digest. "
                    + run.Explain
            );
    }

    /// <summary>
    /// The durable doc has to say what the guard is worth. Two specific lies are
    /// pinned here: "every frame" (the snapshot covers the 12 frames
    /// <see cref="WireSnapshotTests"/> names — several encoder shapes are pinned by
    /// <c>WireConformanceTests</c> instead) and any suggestion that the snapshot
    /// forces a version bump (it does not: the version comes from the API diff, the
    /// wire verdict is measured before the version is asked for but stays warn-only and
    /// never feeds it, and a hand <c>git tag</c> skips the script). A releaser reads
    /// <c>AGENTS.md</c>, so that is where the honest sentence has to be.
    /// </summary>
    [Test]
    public async Task Agents_md_says_what_the_snapshot_covers_and_what_it_does_not_force()
    {
        var repoRoot = ReleaseScriptProbe.FindRepositoryRoot();
        await Assert
            .That(repoRoot)
            .IsNotNull()
            .Because("AGENTS.md must be reachable from the test output directory");

        var agents = await File.ReadAllTextAsync(Path.Combine(repoRoot!, "AGENTS.md"));
        var release = Section(agents, "## Releasing");

        await Assert
            .That(release)
            .Contains(WireSnapshotTests.SnapshotRelativePath)
            .Because("the release section must name the snapshot a releaser may have to touch");
        await Assert
            .That(release)
            .Contains("12 frames")
            .Because(
                "the snapshot is a named slice of the encoder's output, and the doc must "
                    + "say so instead of claiming completeness"
            );
        await Assert
            .That(release)
            .DoesNotContain("every frame")
            .Because(
                "the snapshot does not cover every frame the encoder can emit - "
                    + "\"result\":null, the quoted-string params fallback, surrogate "
                    + "substitution and multi-line normalisation are pinned by "
                    + "WireConformanceTests, not by the fixture"
            );
        await Assert
            .That(release)
            .Contains("does not force a version bump")
            .Because(
                "the snapshot forces an edit and a visible diff; it does not force an x "
                    + "bump, and nothing in CI ties the two together"
            );
        await Assert
            .That(release)
            .Contains("wire-change:")
            .Because("the obligation has to stay named where a releaser reads it");
        await Assert
            .That(release)
            .Contains("PicoAgent.Binding")
            .Because("the lockstep consumer is part of the obligation");
        await Assert
            .That(release)
            .Contains("checked by a human")
            .Because(
                "the two obligations are discipline, not enforcement; saying so is the "
                    + "point of the paragraph"
            );
    }

    /// <summary>Collapse an expectation to LF. <see cref="ScriptRun.Decision"/> reports
    /// LF, and a raw string literal inherits the SOURCE file's line endings - so without
    /// this a CRLF worktree would fail on a line-ending mismatch that has nothing to do
    /// with the script.</summary>
    private static string Normalise(string text) => text.ReplaceLineEndings("\n");

    /// <summary>The text of one <c>## heading</c> section, up to the next one, with
    /// line wrapping collapsed: the assertions are about what the section SAYS, so a
    /// reflow of the paragraph must not turn this test red.</summary>
    private static string Section(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;
        var next = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        var section = next < 0 ? text[start..] : text[start..next];
        return Regex.Replace(section, @"\r?\n\s*", " ");
    }
}

/// <summary>
/// <c>[SkipUnlessReleaseScriptHost]</c> — a loud skip when no PowerShell to run the
/// script with. Everything else about this test is a hard failure: a missing script,
/// a sandbox that blocks process launch, a fixture repo git refuses to build all
/// fail, because each of them means the release half lost its only behavioural
/// check. Only "this machine has no <c>pwsh</c>" is an environment fact worth
/// skipping over — and it says what the run loses.
/// </summary>
internal sealed class SkipUnlessReleaseScriptHostAttribute() : SkipAttribute(UnavailableReason)
{
    /// <summary>The reason, repeated in the skip line with the test name appended.</summary>
    public const string UnavailableReason =
        "SKIPPED - no 'pwsh' (PowerShell 7+) on PATH, so scripts/release.ps1 cannot be RUN. "
        + "This run loses the only behavioural check of the release half of the wire guard: "
        + "the four `wire changed` verdicts (absent / unchanged / changed / UNPINNED) and the "
        + "wire-change:/lockstep obligation it prints. The snapshot half (WireSnapshotTests) "
        + "still ran, so the wire is still pinned; what is unproven here is that the release "
        + "dry run says so. Install PowerShell 7 to recover the coverage.";

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!ReleaseScriptProbe.Available);

    /// <inheritdoc />
    protected override string GetSkipReason(TestRegisteredContext context) =>
        $"{UnavailableReason} Test: {context.TestName}.";
}

/// <summary>
/// A throwaway repository that <c>scripts/release.ps1</c> can be RUN against (T7.7,
/// fix round 1): the real script byte for byte, real <c>git</c>, and a stubbed
/// <c>dotnet</c> that reports an unchanged API and a fixed next version.
/// <para>
/// <b>Why a sandbox and not the real repository.</b> A <c>-DryRun</c> against this
/// worktree needs a Release build of every packable project (<c>Invoke-Build</c> runs
/// before the decision is printed), which is minutes per run and packs a new version
/// of the real assemblies — not something a test may do. The sandbox keeps the parts
/// the assertion is about (the script's own logic, its <c>git</c> calls, its file
/// hashing and its output) real, and stubs only the one thing that is not: the API
/// diff, which is already covered by the api-surface tool's own tests and by the
/// published baselines.
/// </para>
/// <para>
/// <b>What is deliberately real: the script and git.</b> The script is
/// <see cref="File.Copy(string, string)"/>d from this repository on every run, so the
/// text under test is the text that ships; <c>git</c> is the real one, on a real
/// repository with a real annotated tag, configured <c>core.autocrlf=false</c> so the
/// blob comparison sees the bytes we wrote rather than a checkout transform — which is
/// also how the fixture ends up proving the <c>-text</c> rule is about checkout, not
/// about <c>Get-FileHash</c>.
/// </para>
/// <para>
/// The stub also swallows <c>dotnet build</c>, so a stale <c>obj/</c> or a missing
/// SDK cannot make this test flaky — and the real tool is never invoked, so the
/// sandbox cannot publish, restore or touch <c>api/</c>.
/// </para>
/// </summary>
internal sealed class ReleaseScriptProbe : IDisposable
{
    /// <summary>What the stubbed <c>next-version</c> answers, so the Decision block
    /// is deterministic.</summary>
    internal const string StubNextVersion = "2026.8.0";

    /// <summary>The tag the sandbox starts from. The NAME is the real last release so the
    /// printed lines look like the ones an operator sees, but it is a synthetic tag inside
    /// the throwaway repository: it points at a commit that PREDATES the snapshot, which is
    /// every release cut before T7.7.</summary>
    internal const string LastTag = "v2026.5.1";

    /// <summary>The path the script looks for, taken from the test that pins the
    /// file — one constant, so a move breaks the guard instead of the guard's test.</summary>
    private const string SnapshotRelativePath = WireSnapshotTests.SnapshotRelativePath;

    private const string TimeoutMessage =
        "the sandbox run is a REAL process launch, not a string match. A sandbox or an "
        + "antivirus that blocks spawning pwsh/git must fail this test loudly - a silent "
        + "skip would return the release half of the guard to 'checked by a grep'";

    private static readonly Lazy<(string Executable, string Version)?> Host = new(FindHost);
    private static readonly Lazy<string?> Repository = new(FindRepositoryRoot);
    private static readonly Regex Ansi = new("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private readonly string _workRoot;
    private readonly string _repo;
    private readonly string _stubDirectory;
    private readonly string _executable;
    private readonly string _version;

    /// <summary>Is there a <c>pwsh</c> to run the script with? The probe is cached:
    /// a skip decision is made per test at registration.</summary>
    internal static bool Available => Host.Value is not null;

    /// <summary>The detected host, e.g. <c>pwsh 7.6.7</c> — quoted in failure output.</summary>
    internal string HostDescription => $"{_executable} {_version}";

    private ReleaseScriptProbe(string workRoot, string executable, string version)
    {
        _workRoot = workRoot;
        _repo = Path.Combine(workRoot, "repo");
        _stubDirectory = Path.Combine(workRoot, "bin");
        _executable = executable;
        _version = version;
        Seed();
    }

    /// <summary>Build a sandbox: seeded git repository + stub <c>dotnet</c>.</summary>
    internal static ReleaseScriptProbe Create()
    {
        var host =
            Host.Value
            ?? throw new InvalidOperationException(
                SkipUnlessReleaseScriptHostAttribute.UnavailableReason
            );
        var repository =
            Repository.Value
            ?? throw new InvalidOperationException(
                "scripts/release.ps1 was not found above the test output directory, so the "
                    + "release half of the wire guard has nothing to run"
            );
        var workRoot = Path.Combine(
            Path.GetTempPath(),
            "piconode-release-probe-" + Guid.NewGuid().ToString("N")[..12]
        );
        Directory.CreateDirectory(workRoot);
        var probe = new ReleaseScriptProbe(workRoot, host.Executable, host.Version);
        File.Copy(
            Path.Combine(repository, "scripts", "release.ps1"),
            probe.ScriptPath,
            overwrite: true
        );
        return probe;
    }

    /// <summary>Walk up from the test output directory to the repository root (the
    /// directory that holds <c>scripts/release.ps1</c>).</summary>
    internal static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "release.ps1")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private string ScriptPath => Path.Combine(_repo, "scripts", "release.ps1");

    private string SandboxSnapshot => Path.Combine(_repo, SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar));

    // -- scenario steps ------------------------------------------------------

    /// <summary>Move <see cref="LastTag"/> onto HEAD, so the next run compares against
    /// a tag that contains the snapshot.</summary>
    internal void Retag(string tag) => Git("tag", "-f", "-a", tag, "-m", tag);

    /// <summary>Change the snapshot in the WORKTREE only — the shape of a developer
    /// regenerating the fixture before committing it, which is the moment the release
    /// dry run has to speak up.</summary>
    internal void MoveSnapshot() =>
        WriteSnapshot("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}\n{\"jsonrpc\":\"2.0\",\"method\":\"pong\"}\n");

    /// <summary>Remove the snapshot entirely: the wire must be reported UNPINNED.</summary>
    internal void DeleteSnapshot() => File.Delete(SandboxSnapshot);

    /// <summary>The snapshot's path in the sandbox, for the stub's rewrite hook.</summary>
    internal string SnapshotPath => SandboxSnapshot;

    /// <summary>Write <paramref name="content"/> to a SIBLING file the stub can copy over
    /// the snapshot at the moment <c>next-version</c> is called — the ordering probe.</summary>
    internal string WriteReplacementSnapshotForSwap(string content)
    {
        var path = Path.Combine(_workRoot, "rewritten-snapshot.ndjson");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The sha256 of the sandbox snapshot as it sits on disk — the digest the
    /// script must print.</summary>
    internal string SnapshotHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(SandboxSnapshot)));

    // -- running -------------------------------------------------------------

    /// <summary>Run <c>release.ps1 -DryRun</c> against the sandbox.</summary>
    /// <param name="environment">Extra variables for the child — the stub reads them, so
    /// a test can make the script's own <c>next-version</c> call have an observable side
    /// effect.</param>
    internal ScriptRun Run(IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add("-DryRun");
        psi.ArgumentList.Add("-RepoRoot");
        psi.ArgumentList.Add(_repo);
        UseStubDotnet(psi);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        var outcome = Execute(psi, TimeSpan.FromMinutes(2), $"release.ps1 -DryRun ({HostDescription})");
        return new ScriptRun(outcome.Exit, Strip(outcome.Out), Strip(outcome.Err), this);
    }

    // -- seeding -------------------------------------------------------------

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "scripts", "api-surface"));
        Directory.CreateDirectory(Path.Combine(_repo, "src", "Fake", "bin", "Release", "net10.0"));
        Directory.CreateDirectory(Path.Combine(_repo, "api"));
        Directory.CreateDirectory(Path.GetDirectoryName(SandboxSnapshot)!);
        Directory.CreateDirectory(_stubDirectory);

        // The api-surface tool just has to exist: the stub dreamt up below answers
        // `build` and `dump`, so the real tool (and the real SDK) is never touched.
        File.WriteAllText(
            Path.Combine(_repo, "scripts", "api-surface", "ApiSurface.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>

            """
        );
        File.WriteAllText(
            Path.Combine(_repo, "src", "Fake", "Fake.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsPackable>true</IsPackable>
                <PackageId>Fake</PackageId>
                <AssemblyName>Fake</AssemblyName>
              </PropertyGroup>
            </Project>

            """
        );
        // Get-Surface finds the built assembly by name under bin/Release.
        File.WriteAllBytes(
            Path.Combine(_repo, "src", "Fake", "bin", "Release", "net10.0", "Fake.dll"),
            []
        );
        File.WriteAllText(Path.Combine(_repo, "api", "Fake.public.txt"), "M: Fake.Types.Fake\n");

        WriteStubDotnet();

        Git("init", "-q");
        Git("symbolic-ref", "HEAD", "refs/heads/main");
        // Hermetic bytes: the script's own git calls inherit this repository config, so
        // no global core.autocrlf can make `git diff` warn about (or rewrite) the snapshot.
        Git("config", "core.autocrlf", "false");
        Git("add", "-A");
        Git("commit", "-qm", "probe: seed");
        // The tag predates the snapshot - the shape of EVERY release cut before T7.7,
        // and the reason `absent` has to exist: the comparison is unmeasurable, and the
        // script has to say so instead of reporting a comfortable "unchanged".
        Git("tag", "-a", LastTag, "-m", LastTag);

        // ...and the snapshot arrives in the next commit, so the worktree holds it (the
        // hash is printable) while the tag does not (the verdict is `absent`).
        WriteSnapshot("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}\n");
        Git("add", "-A");
        Git("commit", "-qm", "probe: pin the snapshot");
    }

    /// <summary>
    /// The stub <c>dotnet</c>: <c>build</c> and <c>dump</c> succeed silently, <c>diff</c>
    /// reports nothing added and nothing removed (an unchanged API, so the version rule
    /// takes its <c>y</c> branch), <c>next-version</c> answers
    /// <see cref="StubNextVersion"/>. One file per platform so the same directory works
    /// from pwsh on Windows (<c>.cmd</c> via PATHEXT) and on Linux/macOS (extensionless
    /// shim).
    /// </summary>
    private void WriteStubDotnet()
    {
        // The stub does one extra thing besides answering: when WIRE_PROBE_SWAP /
        // WIRE_PROBE_CONTENT are set, it REWRITES the snapshot as it answers
        // `next-version`. That is how the ORDER of the two is tested without clock
        // tricks - see Wire_hash_is_measured_before_the_version_is_asked_for.
        //
        // A literal `"""` inside a raw literal would end it, and .cmd needs `%...%`:
        // hence the 4-quote delimiter below.
        File.WriteAllText(
            Path.Combine(_stubDirectory, "dotnet.cmd"),
            $$""""
                @echo off
                if "%2"=="next-version" goto next
                if "%2"=="diff" goto diff
                exit /b 0
                :next
                if not "%WIRE_PROBE_SWAP%"=="" copy /y "%WIRE_PROBE_CONTENT%" "%WIRE_PROBE_SWAP%" >nul 2>&1
                echo {{StubNextVersion}}
                exit /b 0
                :diff
                echo added=0 removed=0
                exit /b 0

                """".ReplaceLineEndings()
        );

        var unix = Path.Combine(_stubDirectory, "dotnet");
        File.WriteAllText(
            unix,
            $$""""
                #!/bin/sh
                case "$2" in
                  next-version)
                    if [ -n "$WIRE_PROBE_SWAP" ]; then cp "$WIRE_PROBE_CONTENT" "$WIRE_PROBE_SWAP"; fi
                    echo {{StubNextVersion}} ;;
                  diff) echo "added=0 removed=0" ;;
                esac
                exit 0

                """"
        );
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                unix,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
    }

    private void WriteSnapshot(string content) =>
        File.WriteAllBytes(SandboxSnapshot, System.Text.Encoding.UTF8.GetBytes(content));

    private void Git(params string[] arguments)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        // Deterministic, hermetic history: no user/system config beyond what is
        // passed here (a signed-commit default would fail the seed), no EOL
        // transform, no inherited GIT_DIR pointing at the real worktree.
        foreach (var config in new[]
        {
            "core.autocrlf=false",
            "core.safecrlf=false",
            "commit.gpgsign=false",
            "tag.gpgsign=false",
            "user.name=release-script-probe",
            "user.email=release-script-probe@example.invalid",
        })
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(config);
        }
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        SanitizeEnvironment(psi);

        var outcome = Execute(psi, TimeSpan.FromMinutes(1), $"git {string.Join(' ', arguments)}");
        if (outcome.Exit != 0)
            throw new InvalidOperationException(
                $"the sandbox repository could not be built: git {string.Join(' ', arguments)} "
                    + $"exited {outcome.Exit}.{Environment.NewLine}{outcome.Out}{outcome.Err}"
            );
    }

    /// <summary>Put the stub first on <c>PATH</c> for the child, and drop the git
    /// variables that would otherwise redirect the script's git at the real worktree.</summary>
    private void UseStubDotnet(ProcessStartInfo psi)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        psi.Environment["PATH"] = _stubDirectory + Path.PathSeparator + path;
        psi.Environment["Path"] = _stubDirectory + Path.PathSeparator + path;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        SanitizeEnvironment(psi);
    }

    private static void SanitizeEnvironment(ProcessStartInfo psi)
    {
        psi.Environment.Remove("GIT_DIR");
        psi.Environment.Remove("GIT_WORK_TREE");
        psi.Environment.Remove("GIT_INDEX_FILE");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    private static (string Executable, string Version)? FindHost()
    {
        foreach (var executable in new[] { "pwsh", "pwsh.exe" })
        {
            try
            {
                var psi = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("$PSVersionTable.PSVersion.ToString()");
                var outcome = Execute(psi, TimeSpan.FromSeconds(30), $"'{executable}' -Command");
                var version = outcome.Out.Trim();
                if (outcome.Exit == 0 && version.Length > 0)
                    return (executable, version);
            }
            catch (Exception)
            {
                // not on PATH, or a shim that cannot start: try the next name
            }
        }
        return null;
    }

    private static (int Exit, string Out, string Err) Execute(
        ProcessStartInfo psi,
        TimeSpan timeout,
        string what
    )
    {
        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"{what} could not be started. {TimeoutMessage}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // best effort: the timeout is the report
            }
            throw new InvalidOperationException(
                $"{what} did not finish within {timeout.TotalSeconds:0}s. {TimeoutMessage}"
            );
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string Strip(string text) => Ansi.Replace(text, string.Empty);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_workRoot))
                return;
            // git writes its objects READ-ONLY, and Directory.Delete refuses those on
            // Windows - which is why three probe runs used to leave three directories
            // behind per test run. Clear the flag first, then delete.
            foreach (var file in Directory.EnumerateFiles(_workRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_workRoot, recursive: true);
        }
        catch (Exception)
        {
            // a lingering temp directory must not fail the run
        }
    }
}

/// <summary>One run's output, already stripped of ANSI colour.</summary>
internal sealed record ScriptRun(int ExitCode, string StandardOutput, string StandardError, ReleaseScriptProbe Probe)
{
    /// <summary>
    /// The <c>== Decision</c> block: everything the script prints between that header
    /// and the blank line before the dry-run trailer. Asserted as a whole, so a
    /// dropped line, a reworded note or an extra verdict fails — and a script with no
    /// decision block at all returns a sentinel that names the failure instead of an
    /// empty string that might compare equal to nothing.
    /// </summary>
    internal string Decision()
    {
        var lines = StandardOutput.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, static line => line.Trim() == "== Decision");
        if (start < 0)
            return "<the script printed no '== Decision' section - the wire half of the release guard is gone>";

        var block = new List<string>();
        for (var i = start + 1; i < lines.Length && lines[i].Length > 0; i++)
            block.Add(lines[i].TrimEnd());
        return string.Join('\n', block);
    }

    /// <summary>The whole output plus both streams — the failure detail, so a red test
    /// says what the script actually printed.</summary>
    internal string Explain =>
        $"`release.ps1 -DryRun` (host {Probe.HostDescription}) exited {ExitCode}.{Environment.NewLine}"
        + $"stdout:{Environment.NewLine}{StandardOutput}{Environment.NewLine}"
        + $"stderr:{Environment.NewLine}{StandardError}";
}
