namespace PicoNode.Tests;

/// <summary>
/// Guards the READMEs against drifting from the code they describe: the
/// PicoNode.Abs target framework, the stack size claim, and the PicoWeb.Gen
/// responsibilities (it emits diagnostics, not generated source).
/// </summary>
public sealed class ReadmeAccuracyTests
{
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string[] Readmes =>
        Directory.GetFiles(RepoRoot, "README*.md").OrderBy(static p => p).ToArray();

    [Test]
    public async Task No_readme_claims_PicoNode_Abs_targets_netstandard2()
    {
        var offenders = Readmes
            .SelectMany(file =>
                File.ReadAllLines(file)
                    .Where(line =>
                        line.Contains("PicoNode.Abs", StringComparison.Ordinal)
                        && (
                            line.Contains("netstandard2.0", StringComparison.Ordinal)
                            || line.Contains("Standard 2.0", StringComparison.Ordinal)
                        )
                    )
                    .Select(line => $"{Path.GetFileName(file)}: {line}")
            )
            .ToArray();

        await Assert.That(offenders.Length).IsEqualTo(0);
    }

    [Test]
    public async Task No_readme_claims_15K_lines()
    {
        var offenders = Readmes
            .SelectMany(file =>
                File.ReadAllLines(file)
                    .Where(line => line.Contains("15K", StringComparison.Ordinal))
                    .Select(line => $"{Path.GetFileName(file)}: {line}")
            )
            .ToArray();

        await Assert.That(offenders.Length).IsEqualTo(0);
    }

    [Test]
    public async Task No_readme_bullet_claims_DTO_attribute_generation()
    {
        // The old bullets attributed `[PicoJsonSerializable]` generation to the
        // source generators; DTO serializers come from PicoJetson.Gen, not from
        // Controllers.Gen/PicoWeb.Gen.
        var offenders = Readmes
            .SelectMany(file =>
                File.ReadAllLines(file)
                    .Where(line =>
                        line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                        && line.Contains("[PicoJsonSerializable]", StringComparison.Ordinal)
                    )
                    .Select(line => $"{Path.GetFileName(file)}: {line}")
            )
            .ToArray();

        await Assert.That(offenders.Length).IsEqualTo(0);
    }

    [Test]
    public async Task English_readme_documents_picoweb_gen_diagnostics()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));

        await Assert.That(text).Contains("build-time diagnostic (PWR001)");
        await Assert.That(text).Contains("PicoJetson.Gen");
    }
}
