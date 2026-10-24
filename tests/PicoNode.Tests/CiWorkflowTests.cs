using System.Text.Json;
using System.Text.RegularExpressions;

namespace PicoNode.Tests;

/// <summary>
/// Contract tests for repository CI/build configuration: every test project
/// must be executed by CI, no workflow step may silently lose a command to a
/// duplicate YAML key, and the SDK must be pinned to the supported band.
/// </summary>
public sealed class CiWorkflowTests
{
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string CiPath => Path.Combine(RepoRoot, ".github", "workflows", "ci.yml");

    private static readonly string[] TestProjects =
    [
        "tests/PicoNode.Tests/PicoNode.Tests.csproj",
        "tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj",
        "tests/PicoJsonRpc.Tests/PicoJsonRpc.Tests.csproj",
        "tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj",
        "tests/PicoWeb.DI.Tests/PicoWeb.DI.Tests.csproj",
        "tests/PicoNode.Smoke/PicoNode.Smoke.csproj",
        "tests/PicoNode.PerfHarness/PicoNode.PerfHarness.csproj",
        "tests/PicoWeb.Tests/PicoWeb.Tests.csproj",
        "tests/PicoWeb.Integration.Tests/PicoWeb.Integration.Tests.csproj",
    ];

    [Test]
    public async Task Every_test_project_has_a_ci_step()
    {
        var ci = File.ReadAllText(CiPath);

        var missing = TestProjects.Where(p => !ci.Contains(p, StringComparison.Ordinal)).ToArray();

        await Assert.That(missing.Length).IsEqualTo(0);
    }

    [Test]
    public async Task No_duplicate_keys_within_a_ci_step()
    {
        // A duplicate `run:` key silently discards the first command (YAML
        // mapping semantics) — exactly how PicoNode.Http.Tests stopped running.
        var duplicates = FindDuplicateStepKeys(File.ReadAllLines(CiPath));

        await Assert.That(duplicates.Count).IsEqualTo(0);
    }

    private static string AotScriptPath =>
        Path.Combine(RepoRoot, "scripts", "test-aot-publish.ps1");

    private static string AotVerifierProgramPath =>
        Path.Combine(RepoRoot, "tests", "PicoWeb.AotVerify", "Program.cs");

    [Test]
    public async Task Contains_call_with_string_argument_requires_an_argument_not_a_mention()
    {
        // A C# comment inside the generated here-string is yielded as a "code line";
        // only an invocation carrying a string argument proves the entry point is
        // actually called (a mere `api.RunAsync(...)` mention must not satisfy it).
        await Assert
            .That(
                ContainsCallWithStringArgument(
                    "// Also exercise the documented api.RunAsync(...) entry point",
                    "api.RunAsync"
                )
            )
            .IsFalse();
        await Assert
            .That(
                ContainsCallWithStringArgument(
                    "var t = api.RunAsync(\"http://127.0.0.1:0\", ct);",
                    "api.RunAsync"
                )
            )
            .IsTrue();
        await Assert
            .That(
                ContainsCallWithStringArgument(
                    "var t = api.RunAsync(\n        \"http://127.0.0.1:0\", ct);",
                    "api.RunAsync"
                )
            )
            .IsTrue()
            .Because("the matcher must accept a multi-line call, not only a single-line one");
    }

    [Test]
    public async Task Aot_verifier_exercises_the_runasync_entry_point()
    {
        // WebApiApp.RunAsync (and its ProcessShutdown platform events) must stay in
        // the ILC reachable set and actually run under AOT. The committed verifier
        // project publishes on every CI runner; requirement: a real call carrying a
        // string argument (a mere mention in a comment must not satisfy it).
        var program = File.ReadAllText(AotVerifierProgramPath);

        await Assert.That(ContainsCallWithStringArgument(program, "api.RunAsync")).IsTrue();
    }

    [Test]
    public async Task Aot_verifier_binds_an_ephemeral_port()
    {
        // A fixed port makes the gate fail when anything else holds it (notably a
        // second local run); the verifier binds port 0 and reads the assigned port
        // back from WebServer.LocalEndPoint (multi-line bind + ranged read-back).
        var program = File.ReadAllText(AotVerifierProgramPath);

        await Assert.That(program.Contains("9876", StringComparison.Ordinal)).IsFalse();
        await Assert
            .That(Regex.IsMatch(program, @"IPEndPoint\(\s*System\.Net\.IPAddress\.Loopback"))
            .IsTrue();
        await Assert.That(Regex.IsMatch(program, @"\.LocalEndPoint!\)\.Port")).IsTrue();
    }

    [Test]
    public async Task Code_line_filter_ignores_line_and_block_comments()
    {
        const string sample = """
            # line comment with $env:TEMP
            <# block comment with $env:TEMP
               still inside the block #>
            $x = 1
            $y = 2
            """;

        var code = string.Join('\n', CodeLines(sample));

        await Assert.That(code.Contains("$env:TEMP", StringComparison.Ordinal)).IsFalse();
        await Assert.That(code).Contains("$x = 1");
        await Assert.That(code).Contains("$y = 2");
    }

    [Test]
    public async Task Code_line_filter_does_not_let_a_line_comment_open_block_state()
    {
        // Regression: a `<#` inside a `#` line comment used to poison the state and
        // silently drop every following code line, blinding the tripwire.
        const string sample = """
            # see <# note
            $after = 1
            """;

        var code = CodeLines(sample).ToArray();

        await Assert
            .That(code.Any(static line => line.Contains("$after = 1", StringComparison.Ordinal)))
            .IsTrue();
    }

    [Test]
    public async Task Code_line_filter_strips_multiple_block_comments_on_one_line()
    {
        const string sample = """
            $x = 1 <# a #> <# b $env:TEMP #>
            """;

        var code = CodeLines(sample).ToArray();

        await Assert
            .That(code.Any(static line => line.Contains("$env:TEMP", StringComparison.Ordinal)))
            .IsFalse();
        await Assert
            .That(code.Any(static line => line.Contains("<#", StringComparison.Ordinal)))
            .IsFalse();
        await Assert
            .That(code.Any(static line => line.Contains("$x = 1", StringComparison.Ordinal)))
            .IsTrue();
    }

    [Test]
    public async Task Code_line_filter_drops_comment_only_lines()
    {
        const string sample = """
            # only a comment
            <# only a block #>
            $z = 3
            """;

        var code = CodeLines(sample).ToArray();

        await Assert.That(code.Length).IsEqualTo(1);
        await Assert.That(code[0].Trim()).IsEqualTo("$z = 3");
    }

    [Test]
    public async Task Aot_script_publishes_the_committed_verifier_project()
    {
        // The gate publishes a committed project inside the repository. A generated
        // temp project outside the tree made MSBuild resolve the referenced
        // projects' own relative references against the wrong base on macOS/Linux
        // (MSBuild MSB3202), and $env:TEMP is unset on those runners anyway.
        var script = File.ReadAllText(AotScriptPath);
        var codeLines = CodeLines(script);

        await Assert
            .That(
                codeLines.Any(static line => line.Contains("$env:TEMP", StringComparison.Ordinal))
            )
            .IsFalse();
        await Assert.That(script).Contains("tests/PicoWeb.AotVerify");
        await Assert
            .That(script.Contains("Set-Content", StringComparison.Ordinal))
            .IsFalse()
            .Because("the gate must publish the committed project, not generate one");
    }

    [Test]
    public async Task Aot_publish_script_is_wired_into_ci()
    {
        // The AOT integration script publishes PicoWeb as a native binary and
        // probes it over HTTP; without a CI step the gate silently rots (it was
        // broken from the day it was added until it was fixed).
        var ci = File.ReadAllText(CiPath);

        await Assert.That(ci).Contains("scripts/test-aot-publish.ps1");
    }

    [Test]
    public async Task Every_workflow_that_runs_the_rpc_suite_installs_node()
    {
        // PicoJsonRpc.Tests drives REAL children (`node <fixture>`), and its guard
        // answers a missing runtime with a loud skip naming the 20 tests it loses.
        // Loud is not enough in CI: a runner without node reports the same green
        // run with a fifth of the suite skipped - the silent coverage loss the node
        // guard exists to prevent, merely relocated from "no guard at all" to
        // "guard plus no runtime". The rule is DERIVED, not hard-coded to one file:
        // any workflow that names the suite must install the runtime before running
        // it (ci.yml validates a branch, release.yml gates a NuGet publish - both
        // run the suite, so both need the step; a release that skipped 20 tests
        // while calling the version verified is the worse of the two).
        var workflowDir = Path.Combine(RepoRoot, ".github", "workflows");
        var runsSuite = Directory
            .EnumerateFiles(workflowDir, "*.yml")
            .Order(StringComparer.Ordinal)
            .Where(static w =>
                File.ReadAllText(w).Contains("tests/PicoJsonRpc.Tests/", StringComparison.Ordinal)
            )
            .ToArray();

        // Non-vacuity: if the suite path were renamed (or this walk regressed) the
        // lists below would be empty and the test would prove nothing.
        await Assert
            .That(runsSuite.Select(Path.GetFileName).ToArray())
            .IsNotEmpty()
            .Because("at least one workflow is expected to run the JSON-RPC suite");

        var withoutNode = runsSuite
            .Where(static w =>
                !File.ReadAllText(w).Contains("uses: actions/setup-node@", StringComparison.Ordinal)
            )
            .Select(static w => Path.GetFileName(w))
            .ToArray();

        await Assert
            .That(withoutNode)
            .IsEmpty()
            .Because(
                "a workflow that runs PicoJsonRpc.Tests must install node first, otherwise "
                    + "its 21 node-dependent tests skip in CI instead of running"
            );

        // ...and the major is PINNED to a number: `latest` or `lts/*` would move the
        // runtime under the suite between runs, so a wire regression would be
        // indistinguishable from a runtime change.
        var unpinned = runsSuite
            .SelectMany(static w =>
                File.ReadAllLines(w)
                    .Where(static line => line.Contains("node-version:", StringComparison.Ordinal))
                    .Where(static line => !Regex.IsMatch(line, @"node-version:\s*'?\d+(\.\d+)*'?"))
                    .Select(line => $"{Path.GetFileName(w)}: {line.Trim()}")
            )
            .ToArray();

        await Assert
            .That(unpinned)
            .IsEmpty()
            .Because("the node major must be a pinned number, not `latest` or `lts/*`");
    }

    [Test]
    public async Task E2e_sse_script_is_wired_into_ci()
    {
        // The Python SSE end-to-end harness must run in CI — it exercises the
        // full stack (TCP -> HTTP -> PicoWeb -> SSE) beyond unit coverage.
        var ci = File.ReadAllText(CiPath);

        await Assert.That(ci).Contains("tests/PicoNode.E2E/test_sse_streaming.py");
    }

    [Test]
    public async Task Global_json_pins_sdk_10()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot, "global.json"))
        );

        await Assert.That(document.RootElement.TryGetProperty("sdk", out var sdk)).IsTrue();
        await Assert.That(sdk.GetProperty("version").GetString()).StartsWith("10.");
    }

    /// <summary>
    /// True when <paramref name="text"/> contains an actual invocation of
    /// <paramref name="memberName"/> with a string argument — a mere mention in a
    /// comment (e.g. <c>api.RunAsync(...)</c>) does not count.
    /// </summary>
    private static bool ContainsCallWithStringArgument(string text, string memberName) =>
        Regex.IsMatch(text, Regex.Escape(memberName) + "\\(\\s*\"");

    /// <summary>
    /// Script code lines with line comments and block comments removed. This is a
    /// tripwire, not a PowerShell parser: trailing comments and quoted strings are
    /// deliberately out of scope (the contract tests only need to catch real use
    /// of forbidden constructs).
    /// </summary>
    private static IEnumerable<string> CodeLines(string script)
    {
        var inBlockComment = false;
        foreach (var line in script.Split('\n'))
        {
            var text = line;

            if (inBlockComment)
            {
                var resume = text.IndexOf("#>", StringComparison.Ordinal);
                if (resume < 0)
                {
                    continue;
                }

                text = text[(resume + 2)..];
                inBlockComment = false;
            }

            // A full-line comment is never code and must not open block-comment
            // state (a `<#` inside a `#` comment is just text).
            if (text.TrimStart().StartsWith('#'))
            {
                continue;
            }

            // Strip every block comment on the line; an unclosed one carries the
            // state over to the following lines.
            while (true)
            {
                var blockStart = text.IndexOf("<#", StringComparison.Ordinal);
                if (blockStart < 0)
                {
                    break;
                }

                var blockEnd = text.IndexOf("#>", blockStart + 2, StringComparison.Ordinal);
                if (blockEnd < 0)
                {
                    text = text[..blockStart];
                    inBlockComment = true;
                    break;
                }

                text = text[..blockStart] + text[(blockEnd + 2)..];
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    private static List<string> FindDuplicateStepKeys(string[] lines)
    {
        var duplicates = new List<string>();
        var marker = new Regex(@"^(\s*)- ");
        var keyRegex = new Regex(@"^(\s*)([A-Za-z0-9_-]+):");

        string? currentStep = null;
        var keyIndent = -1;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        void FinishStep()
        {
            if (currentStep is null)
                return;

            foreach (var (key, count) in counts)
            {
                if (count > 1)
                {
                    duplicates.Add($"{currentStep}: duplicate key '{key}' ({count}x)");
                }
            }

            counts.Clear();
        }

        foreach (var line in lines)
        {
            var markerMatch = marker.Match(line);
            if (markerMatch.Success && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                FinishStep();
                currentStep = line.Trim();
                keyIndent = markerMatch.Groups[1].Length + 2;
                continue;
            }

            if (currentStep is null)
                continue;

            var keyMatch = keyRegex.Match(line);
            if (keyMatch.Success && keyMatch.Groups[1].Length == keyIndent)
            {
                var key = keyMatch.Groups[2].Value;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        FinishStep();
        return duplicates;
    }

    [Test]
    public async Task Tracked_python_scripts_are_referenced()
    {
        // The audit found three one-shot scripts committed from a sibling project
        // (one carried a live API key). They must stay deleted, and any Python file
        // still in the tree must be wired into something that actually runs — a CI
        // workflow, a repository script or AGENTS.md — because a file nobody
        // references is exactly the kind of dead weight that hid the key.
        // Scope note: only *.py is walked, exactly as the brief scopes this task;
        // other languages are not covered by this contract test.
        string[] oneShot =
        [
            "scripts/test_compaction.py",
            "scripts/verify_demo.py",
            "tests/web_test.py",
        ];

        var resurrected = oneShot.Where(p => File.Exists(Path.Combine(RepoRoot, p))).ToArray();

        await Assert
            .That(resurrected)
            .IsEmpty()
            .Because("the one-shot scripts (and the committed key inside one) stay deleted");

        var pythonFiles = PythonFiles().ToArray();

        // Non-vacuity guard: if RepoRoot misresolved or the walk regressed, the
        // unreferenced list below would simply be empty and this test would pass
        // while proving nothing. Both paths are tracked today, so the walk must see
        // them.
        string[] mustBeVisible =
        [
            Path.Combine(RepoRoot, "scripts", "check-autobahn-report.py"),
            Path.Combine(RepoRoot, "tests", "PicoNode.E2E", "test_sse_streaming.py"),
        ];

        var notWalked = mustBeVisible
            .Where(path => !pythonFiles.Contains(path, StringComparer.Ordinal))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToArray();

        await Assert
            .That(notWalked)
            .IsEmpty()
            .Because($"the Python walk must see every known script under '{RepoRoot}'");

        var corpus = string.Join(
            '\n',
            ReferenceCorpus().Select(static path => File.ReadAllText(path))
        );

        var unreferenced = pythonFiles
            .Where(path => !IsReferenced(corpus, Path.GetFileName(path)))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        await Assert
            .That(unreferenced)
            .IsEmpty()
            .Because("a tracked Python script must be referenced by CI, a script or AGENTS.md");
    }

    /// <summary>
    /// True when <paramref name="fileName"/> appears in <paramref name="corpus"/> as
    /// a whole name, not glued to name characters (<c>[A-Za-z0-9_.-]</c>) on either
    /// side. An ordinal substring match is not enough: <c>report.py</c> occurs inside
    /// <c>check-autobahn-report.py</c> and <c>streaming.py</c> inside
    /// <c>test_sse_streaming.py</c>, so a dead script whose name happens to be a
    /// suffix of a wired one would look referenced.
    /// </summary>
    private static bool IsReferenced(string corpus, string fileName) =>
        Regex.IsMatch(
            corpus,
            $@"(?<![\w.-]){Regex.Escape(fileName)}(?![\w.-])",
            RegexOptions.CultureInvariant
        );

    /// <summary>
    /// Live wiring a Python script may legitimately be named in: the CI workflows,
    /// the repository's PowerShell entry points and <c>AGENTS.md</c>. Plan and audit
    /// prose is deliberately out of scope — naming a file in a document does not
    /// make it live (the plans still mention the three deleted scripts).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The repository layout is missing under <see cref="RepoRoot"/>. Filtering with
    /// <see cref="File.Exists"/> alone would silently shrink the corpus until the
    /// contract checked nothing at all.
    /// </exception>
    private static IEnumerable<string> ReferenceCorpus()
    {
        var workflows = Path.Combine(RepoRoot, ".github", "workflows");
        var scripts = Path.Combine(RepoRoot, "scripts");

        if (!Directory.Exists(workflows) || !Directory.Exists(scripts))
        {
            throw new InvalidOperationException(
                $"Repository layout not found under '{RepoRoot}': expected '.github/workflows' and 'scripts'."
            );
        }

        return Directory
            .EnumerateFiles(workflows, "*.yml")
            .Concat(Directory.EnumerateFiles(scripts, "*.ps1"))
            .Append(Path.Combine(RepoRoot, "AGENTS.md"))
            .Append(Path.Combine(scripts, "README.md"))
            .Where(File.Exists);
    }

    /// <summary>
    /// Every <c>*.py</c> file in the working tree, skipping the directories listed in
    /// <see cref="SkippedDirectories"/> (build output, VCS/harness state, installed
    /// toolchains — the checkout also keeps copies of the fixtures under <c>bin/</c>).
    /// </summary>
    private static IEnumerable<string> PythonFiles()
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(RepoRoot));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            if (SkippedDirectories.Contains(directory.Name, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var file in directory.EnumerateFiles("*.py"))
            {
                yield return file.FullName;
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Directories that never hold repository source and would only produce
    /// unsatisfiable matches: VCS metadata (<c>.git</c>), harness/session state
    /// (<c>.superpowers</c>), locally installed toolchains and virtualenvs
    /// (<c>.venv</c>, <c>venv</c>, <c>.tools</c>, <c>node_modules</c>), editor state
    /// (<c>.idea</c>, <c>.vscode</c>, <c>.vs</c>) and build output (<c>bin</c>,
    /// <c>obj</c>, <c>TestResults</c>, <c>artifacts</c>). Matched by name, not by a
    /// leading dot: <c>.github</c> (and <c>.devcontainer</c>, <c>.config</c>) do hold
    /// repository source and must be walked.
    /// </summary>
    private static readonly string[] SkippedDirectories =
    [
        ".git",
        ".superpowers",
        ".tools",
        ".venv",
        ".idea",
        ".vscode",
        ".vs",
        "venv",
        "node_modules",
        "bin",
        "obj",
        "TestResults",
        "artifacts",
    ];
    private static string ReleasePath =>
        Path.Combine(RepoRoot, ".github", "workflows", "release.yml");

    /// <summary>
    /// The ten test projects the release gate must execute — the same set
    /// <c>ci.yml</c> runs, so a version can never ship while a suite is red.
    /// </summary>
    private static readonly string[] ReleaseTestProjects =
    [
        "tests/PicoNode.Tests/PicoNode.Tests.csproj",
        "tests/PicoNode.Http.Tests/PicoNode.Http.Tests.csproj",
        "tests/PicoJsonRpc.Tests/PicoJsonRpc.Tests.csproj",
        "tests/PicoNode.Web.Tests/PicoNode.Web.Tests.csproj",
        "tests/PicoWeb.Tests/PicoWeb.Tests.csproj",
        "tests/PicoWeb.Integration.Tests/PicoWeb.Integration.Tests.csproj",
        "tests/PicoWeb.DI.Tests/PicoWeb.DI.Tests.csproj",
        "tests/PicoNode.Smoke/PicoNode.Smoke.csproj",
        "tests/PicoNode.PerfHarness/PicoNode.PerfHarness.csproj",
        "tests/PicoNode.Docs.Tests/PicoNode.Docs.Tests.csproj",
    ];

    [Test]
    public async Task Release_workflow_verifies_before_publishing()
    {
        // The tag is the release trigger, but release.yml had no test step at all
        // (AGENTS.md and release.ps1 both claimed it ran the tests), and
        // --skip-duplicate turned a re-run — or a workflow_dispatch, where the
        // version resolved to the branch name — into a green run that published
        // nothing. The gate is a `verify` job the `publish` job depends on.
        var release = File.ReadAllText(ReleasePath);
        var verify = TopLevelBlock(release, "verify");

        await Assert
            .That(verify is not null)
            .IsTrue()
            .Because("release.yml must declare a `verify:` job, not only a `publish:` job");

        var publish = TopLevelBlock(release, "publish");

        await Assert.That(publish is not null).IsTrue();
        await Assert
            .That((publish ?? string.Empty).Contains("needs: verify", StringComparison.Ordinal))
            .IsTrue()
            .Because("publish must be gated on verify, otherwise the test run is advisory");

        // `Matches(release, "dotnet test") >= 1` was satisfied by a comment, or by
        // a `dotnet test` sitting in `publish`: the call has to be inside the gate.
        await Assert
            .That(Regex.Matches(WithoutComments(verify ?? string.Empty), "dotnet test").Count >= 1)
            .IsTrue()
            .Because(
                "the verify job itself must run the test suites — a `dotnet test` anywhere else in the file proves nothing"
            );

        // Prose may name the flag — the workflow explains why it is gone — but a
        // line that actually runs must not still carry it.
        await Assert
            .That(
                WithoutCommentLines(release)
                    .Any(static line =>
                        line.Contains("--skip-duplicate", StringComparison.Ordinal)
                    )
            )
            .IsFalse()
            .Because("--skip-duplicate hides an already-published version behind a green run");
    }

    [Test]
    public async Task Release_verify_job_runs_every_test_project_and_asserts_the_version_shape()
    {
        // A gate that runs "the tests" in prose is worth nothing: the verify job
        // must execute all ten suites and reject a ref that is not a release
        // version (a dispatch used to resolve PACKAGE_VERSION to the branch name).
        // Comments are stripped first: a commented-out `grep -Eq` is not a check.
        var release = File.ReadAllText(ReleasePath);
        var verify = WithoutComments(TopLevelBlock(release, "verify") ?? string.Empty);

        var missing = ReleaseTestProjects
            .Where(p => !verify.Contains(p, StringComparison.Ordinal))
            .ToArray();

        await Assert
            .That(missing.Length)
            .IsEqualTo(0)
            .Because(
                $"the verify job must run every test project; missing: {string.Join(", ", missing)}"
            );

        await Assert.That(verify).Contains("dotnet test");
        await Assert
            .That(verify.Contains("grep -Eq", StringComparison.Ordinal))
            .IsTrue()
            .Because("the version must be asserted against a shape, not merely echoed");
        await Assert
            .That(verify.Contains("[0-9]{4}", StringComparison.Ordinal))
            .IsTrue()
            .Because("the release version shape is <four-digit year>.<x>.<y>");
    }

    [Test]
    public async Task Release_dispatch_takes_the_version_from_an_input_not_the_branch_name()
    {
        // On workflow_dispatch GITHUB_REF_NAME is the branch (main), so
        // `PACKAGE_VERSION=${GITHUB_REF_NAME#v}` packed seven packages labelled
        // `main` — and --skip-duplicate made the no-op push look successful.
        var release = File.ReadAllText(ReleasePath);
        var dispatch = TopLevelBlock(release, "workflow_dispatch");

        await Assert.That(dispatch is not null).IsTrue();
        await Assert
            .That((dispatch ?? string.Empty).Contains("version:", StringComparison.Ordinal))
            .IsTrue()
            .Because("a dispatch carries no tag: the version must be an explicit input");

        await Assert
            .That(release.Contains("inputs.version", StringComparison.Ordinal))
            .IsTrue()
            .Because("both jobs must resolve PACKAGE_VERSION from the input before the ref");

        await Assert
            .That(release.Contains("PACKAGE_VERSION=${GITHUB_REF_NAME#v}", StringComparison.Ordinal))
            .IsFalse()
            .Because("the unguarded tag-only expression is what produced PACKAGE_VERSION=main");
    }

    [Test]
    public async Task Release_verify_job_cannot_swallow_a_failure()
    {
        // The original gate defect was a test step that did not run at all. The
        // other way to lose the gate is subtler: `continue-on-error: true` on the
        // test step, or a `|| true` on any command, keeps every other assertion
        // here green while the job can no longer fail.
        var release = File.ReadAllText(ReleasePath);
        var verify = WithoutComments(TopLevelBlock(release, "verify") ?? string.Empty);

        await Assert
            .That(verify.Contains("continue-on-error", StringComparison.Ordinal))
            .IsFalse()
            .Because("a gate step allowed to fail without failing the job is not a gate");

        await Assert
            .That(Regex.IsMatch(verify, @"\|\|\s*true\b"))
            .IsFalse()
            .Because(
                "`|| true` on a command in the gate hides the failure the gate exists to report"
            );
    }

    [Test]
    public async Task Release_api_surface_step_fails_when_it_compares_nothing()
    {
        // Drift stays a ::warning:: (bumping x is a human decision), but the step
        // used to find no DLL, iterate zero times and report success having
        // compared no assembly at all.
        var release = File.ReadAllText(ReleasePath);
        var verify = TopLevelBlock(release, "verify") ?? string.Empty;
        var step = WithoutComments(
            StepBlock(verify, "Assert the API surface still bumps x") ?? string.Empty
        );

        await Assert
            .That(step.Contains("scripts/api-surface", StringComparison.Ordinal))
            .IsTrue()
            .Because("the step must still dump the public surface");

        await Assert
            .That(step.Contains("-eq 0", StringComparison.Ordinal))
            .IsTrue()
            .Because("comparing zero assemblies must be a hard failure, not a silent pass");

        await Assert
            .That(step.Contains("exit 1", StringComparison.Ordinal))
            .IsTrue()
            .Because("a zero-assembly check that exits 0 is still advisory");
    }

    [Test]
    public async Task Release_env_writes_are_read_by_the_same_job()
    {
        // Jobs do not share $GITHUB_ENV: the version written in `verify` never
        // reached `publish`. A write nothing in the same job reads is dead code
        // that reads like a handover.
        var release = File.ReadAllText(ReleasePath);
        var write = new Regex(@"([A-Z_][A-Z0-9_]*)=[^\n]*>>\s*""?\$GITHUB_ENV");
        var dead = new List<string>();
        var writes = 0;

        foreach (var job in new[] { "verify", "publish" })
        {
            var lines = WithoutCommentLines(TopLevelBlock(release, job) ?? string.Empty).ToArray();
            var reads = lines.Where(line => !write.IsMatch(line)).ToArray();

            foreach (var line in lines.Where(line => write.IsMatch(line)))
            {
                writes++;
                var name = write.Match(line).Groups[1].Value;

                if (
                    !reads.Any(r => r.Contains($"env.{name}", StringComparison.Ordinal))
                    && !reads.Any(r => r.Contains($"${{{name}}}", StringComparison.Ordinal))
                )
                {
                    dead.Add($"{job}: {name}");
                }
            }
        }

        await Assert
            .That(writes >= 1)
            .IsTrue()
            .Because(
                "non-vacuity: `publish` writes PACKAGE_VERSION and its pack steps read it back"
            );

        await Assert
            .That(dead.Count)
            .IsEqualTo(0)
            .Because(
                $"a $GITHUB_ENV write no step in the same job reads is dead code — {string.Join(", ", dead)}"
            );
    }

    [Test]
    public async Task Release_package_assertion_derives_its_count_from_the_workflow()
    {
        // `ls packages/*.nupkg | wc -l | grep -qx 7` pinned the count to a literal
        // kept in step with the seven `dotnet pack` steps by hand; and
        // `unzip -p … | grep -q …` fails a *correct* package under `pipefail`:
        // grep -q exits at the first match, unzip takes SIGPIPE (141).
        var release = File.ReadAllText(ReleasePath);
        var publish = TopLevelBlock(release, "publish") ?? string.Empty;
        var step = WithoutComments(
            StepBlock(publish, "Assert every package was published") ?? string.Empty
        );

        await Assert
            .That(step.Contains(".github/workflows/release.yml", StringComparison.Ordinal))
            .IsTrue()
            .Because("the expected count must be derived from this workflow file, not a literal");

        await Assert
            .That(Regex.IsMatch(step, @"grep\s+-[a-zA-Z]*x[a-zA-Z]*\s+[0-9]+"))
            .IsFalse()
            .Because("a hard-coded count silently disagrees with the number of `dotnet pack` steps");

        await Assert
            .That(Regex.IsMatch(step, @"\|\s*grep\s+-q"))
            .IsFalse()
            .Because(
                "grep -q exits early, the writer dies of SIGPIPE (141) under pipefail, and a correct release goes red"
            );

        await Assert
            .That(Regex.IsMatch(step, @"unzip[^\n]*\|"))
            .IsFalse()
            .Because("the nuspec text must be captured before it is matched, never piped");

        await Assert
            .That(
                step.Contains("$actual", StringComparison.Ordinal)
                    && step.Contains("$expected", StringComparison.Ordinal)
            )
            .IsTrue()
            .Because("a count mismatch must name both numbers");
    }

    /// <summary>
    /// The lines of <paramref name="yaml"/> that are not full-line <c>#</c>
    /// comments: a comment may explain why a construct was removed, a command must
    /// not still carry it.
    /// </summary>
    private static IEnumerable<string> WithoutCommentLines(string yaml) =>
        yaml.Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .Where(static line => !line.TrimStart().StartsWith('#'));

    /// <summary>
    /// The YAML block of a top-level key — a job, or a trigger nested under
    /// <c>on:</c>: the header line plus every following line indented four or more
    /// spaces (blank lines included). <c>null</c> when the key is absent, so an
    /// assertion about a missing job fails loudly instead of matching an empty
    /// string and passing.
    /// </summary>
    private static string? TopLevelBlock(string yaml, string key)
    {
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line == $"  {key}:");

        if (start < 0)
        {
            return null;
        }

        var end = start + 1;
        while (
            end < lines.Length
            && (lines[end].StartsWith("    ", StringComparison.Ordinal) || lines[end].Length == 0)
        )
        {
            end++;
        }

        return string.Join('\n', lines[start..end]).TrimEnd();
    }

    /// <summary>
    /// <paramref name="yaml"/> with its full-line <c>#</c> comments dropped: a
    /// commented-out command must not satisfy a shape assertion.
    /// </summary>
    private static string WithoutComments(string yaml) =>
        string.Join('\n', WithoutCommentLines(yaml));

    /// <summary>
    /// The block of the step named <paramref name="stepName"/> inside a job block
    /// — its <c>- name:</c> line plus every following line up to the next step —
    /// or <c>null</c> when the job declares no such step, so an assertion about a
    /// missing step fails loudly instead of matching an empty string.
    /// </summary>
    private static string? StepBlock(string job, string stepName)
    {
        var lines = job.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line == $"      - name: {stepName}");

        if (start < 0)
        {
            return null;
        }

        var end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("      - ", StringComparison.Ordinal))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]).TrimEnd();
    }

}
