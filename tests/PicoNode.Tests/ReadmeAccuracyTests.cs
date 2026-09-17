using System.Text.RegularExpressions;

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
    public async Task No_readme_claims_the_http_layer_depends_on_the_transport()
    {
        // PicoNode.Http references only PicoNode.Abs — the transport (PicoNode)
        // is a sibling that PicoWeb references directly. The old chain line
        // claimed the false edge "PicoNode.Http  →  PicoNode  →  PicoNode.Abs".
        var pattern = new Regex(@"PicoNode\.Http\s*→\s*PicoNode\s*→");
        var offenders = Readmes
            .SelectMany(file =>
                File.ReadAllLines(file)
                    .Where(line => pattern.IsMatch(line))
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

    [Test]
    public async Task No_readme_claims_17K_lines()
    {
        var offenders = Readmes
            .SelectMany(file =>
                File.ReadAllLines(file)
                    .Where(line => line.Contains("17K", StringComparison.Ordinal))
                    .Select(line => $"{Path.GetFileName(file)}: {line}")
            )
            .ToArray();

        await Assert.That(offenders.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Every_readme_documents_the_async_multipart_api_and_its_limits()
    {
        // The old examples called the nonexistent synchronous Parse(...) and did
        // not mention the parser limits that are now enforced on both body paths.
        var offenders = Readmes
            .SelectMany(file =>
            {
                var text = File.ReadAllText(file);
                var problems = new List<string>();
                if (!text.Contains("MultipartFormDataParser.ParseAsync", StringComparison.Ordinal))
                {
                    problems.Add("missing MultipartFormDataParser.ParseAsync");
                }

                if (text.Contains("MultipartFormDataParser.Parse(", StringComparison.Ordinal))
                {
                    problems.Add("uses the nonexistent synchronous Parse(...) API");
                }

                if (!text.Contains("MaxPartSizeBytes", StringComparison.Ordinal))
                {
                    problems.Add("missing MaxPartSizeBytes");
                }

                if (!text.Contains("MaxTotalSizeBytes", StringComparison.Ordinal))
                {
                    problems.Add("missing MaxTotalSizeBytes");
                }

                if (!text.Contains("({file.Content.Length} bytes)", StringComparison.Ordinal))
                {
                    problems.Add("broken multipart file-size example");
                }

                return problems.Select(p => $"{Path.GetFileName(file)}: {p}");
            })
            .ToArray();

        await Assert.That(offenders.Length).IsEqualTo(0);
    }

    [Test]
    public async Task No_readme_claims_zero_required_runtime_deps()
    {
        // PicoNode packages depend on PicoHex-native libraries (PicoLog.Abs,
        // PicoCfg.Abs, and PicoDI/PicoJetson for PicoWeb) — the claim is only true
        // for Microsoft framework references.
        var offenders = Readmes
            .SelectMany(file =>
                File.ReadAllLines(file)
                    .Where(line =>
                        line.Contains("Zero required runtime deps", StringComparison.Ordinal)
                    )
                    .Select(line => $"{Path.GetFileName(file)}: {line}")
            )
            .ToArray();

        await Assert.That(offenders.Length).IsEqualTo(0);
    }

    private static readonly string[] ModuleCsprojs =
    [
        "src/Network/PicoNode.Abs/PicoNode.Abs.csproj",
        "src/Network/PicoNode/PicoNode.csproj",
        "src/Http/PicoNode.Http/PicoNode.Http.csproj",
        "src/Rpc/PicoJsonRpc/PicoJsonRpc.csproj",
        "src/Web/PicoNode.Web.Session.Abs/PicoNode.Web.Session.Abs.csproj",
        "src/Web/PicoNode.Web/PicoNode.Web.csproj",
        "src/Web/PicoWeb/PicoWeb.csproj",
        "src/Web/Controllers.Gen/Controllers.Gen.csproj",
        "src/Web/PicoWeb.Gen/PicoWeb.Gen.csproj",
    ];

    /// <summary>
    /// Module READMEs state their TFM and PicoHex dependencies; both must match
    /// the csproj (a moved TFM or a pruned reference otherwise goes unnoticed).
    /// </summary>
    [Test]
    public async Task Module_readmes_match_csproj_tfm_and_dependencies()
    {
        var failures = new List<string>();

        foreach (var relativeCsproj in ModuleCsprojs)
        {
            var csprojPath = Path.GetFullPath(Path.Combine(RepoRoot, relativeCsproj));
            var readmePath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "README.md");
            if (!File.Exists(readmePath))
                continue;

            var readme = File.ReadAllText(readmePath);
            var csproj = XDocument.Load(csprojPath);

            var tfmMatch = Regex.Match(readme, @"\*\*TFM\*\*: `([^`]+)`");
            if (tfmMatch.Success)
            {
                var csprojTfm = csproj
                    .Descendants("TargetFramework")
                    .Select(x => x.Value.Trim())
                    .FirstOrDefault();
                if (csprojTfm is not null && csprojTfm != tfmMatch.Groups[1].Value)
                {
                    failures.Add(
                        $"{relativeCsproj}: README TFM '{tfmMatch.Groups[1].Value}' != csproj '{csprojTfm}'"
                    );
                }
            }

            var depMatch = Regex.Match(readme, @"\*\*Dependencies\*\*:([^\r\n]*)");
            if (depMatch.Success)
            {
                var documented = Regex
                    .Matches(depMatch.Groups[1].Value, @"`([^`]+)`")
                    .Select(m => m.Groups[1].Value)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
                var actual = csproj
                    .Descendants("PackageReference")
                    .Select(x => x.Attribute("Include")?.Value)
                    .Where(v => v is not null && v.StartsWith("Pico", StringComparison.Ordinal))
                    .Select(v => v!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                if (!documented.SequenceEqual(actual, StringComparer.Ordinal))
                {
                    failures.Add(
                        $"{relativeCsproj}: README deps [{string.Join(", ", documented)}] != csproj [{string.Join(", ", actual)}]"
                    );
                }
            }
        }

        await Assert
            .That(string.Join(" | ", failures))
            .IsEqualTo("")
            .Because("module README metadata must match the csproj");
    }
}
