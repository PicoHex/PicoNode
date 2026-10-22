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
            .Where(static path => !IsTestFixture(path))
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
    /// Python fixtures are wired by the owning test project's
    /// <c>&lt;None Include="Fixtures\**"&gt;</c> glob rather than by name, so they
    /// cannot be matched against the corpus (the last one, <c>echo_server.py</c>, is
    /// scheduled for deletion by the JSON-RPC conformance plan). Scoped to
    /// <c>tests/</c>: a <c>Fixtures</c> directory anywhere else is ordinary source
    /// and must still be referenced by name.
    /// </summary>
    private static bool IsTestFixture(string path)
    {
        var relative = Path.GetRelativePath(RepoRoot, path);
        var separator = Path.DirectorySeparatorChar;

        return relative.StartsWith($"tests{separator}", StringComparison.Ordinal)
            && relative.Contains($"{separator}Fixtures{separator}", StringComparison.Ordinal);
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

        await Assert
            .That(Regex.Matches(release, "dotnet test").Count >= 1)
            .IsTrue()
            .Because("the release workflow must actually run the test suites");

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
        var release = File.ReadAllText(ReleasePath);
        var verify = TopLevelBlock(release, "verify") ?? string.Empty;

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

}
