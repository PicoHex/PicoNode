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
