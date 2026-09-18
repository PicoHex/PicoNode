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
    public async Task Aot_script_exercises_the_runasync_entry_point()
    {
        // WebApiApp.RunAsync (and its ProcessShutdown platform events) must stay in
        // the ILC reachable set and actually run under AOT — otherwise a trim/AOT
        // regression in the documented entry point would ship unnoticed. Requirement:
        // a code line that invokes RunAsync with a string argument, so neither a
        // PowerShell comment nor a C# comment inside the here-string can satisfy it.
        // Matching over the joined code lines keeps a multi-line call working too.
        var codeLines = CodeLines(File.ReadAllText(AotScriptPath)).ToArray();
        var code = string.Join('\n', codeLines);

        await Assert.That(ContainsCallWithStringArgument(code, "api.RunAsync")).IsTrue();
    }

    [Test]
    public async Task Aot_script_binds_an_ephemeral_port()
    {
        // A fixed port makes the gate fail when anything else holds it (notably a
        // second local run); the verification program binds port 0 and reads the
        // assigned port back from WebServer.LocalEndPoint. The endpoint assertions
        // match the actual call shapes (multi-line bind + ranged read-back).
        var script = File.ReadAllText(AotScriptPath);
        var codeLines = CodeLines(script).ToArray();

        await Assert
            .That(codeLines.Any(static line => line.Contains("9876", StringComparison.Ordinal)))
            .IsFalse();
        await Assert
            .That(Regex.IsMatch(script, @"IPEndPoint\(\s*System\.Net\.IPAddress\.Loopback"))
            .IsTrue();
        await Assert.That(Regex.IsMatch(script, @"\.LocalEndPoint!\)\.Port")).IsTrue();
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
    public async Task Aot_script_is_portable_across_ci_runners()
    {
        // The AOT gate runs on linux/macOS runners too, where $env:TEMP is unset
        // (verified on a Linux environment) and backslash path separators are not
        // separators. Comments are excluded: mentioning the variable in an
        // explanation is fine, using it is not.
        var script = File.ReadAllText(AotScriptPath);
        var codeLines = CodeLines(script);

        await Assert
            .That(
                codeLines.Any(static line => line.Contains("$env:TEMP", StringComparison.Ordinal))
            )
            .IsFalse();
        await Assert.That(script).Contains("[System.IO.Path]::GetTempPath()");
        await Assert.That(script.Contains("\\..\\src\\", StringComparison.Ordinal)).IsFalse();
        await Assert.That(script).Contains("Resolve-Path");
        await Assert
            .That(script.Contains("$PSScriptRoot/../src", StringComparison.Ordinal))
            .IsFalse()
            .Because(
                "a `..` ProjectReference is not normalised on macOS/Linux (MSBuild MSB3202), so the path must be canonical before it is written into the generated csproj"
            );
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
}
