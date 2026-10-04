namespace PicoNode.Docs.Tests;

/// <summary>
/// Anti-drift check for the README code samples: every README's rate-limiting block must equal
/// the marked region in <see cref="ReadmeSamples"/> - the copy that actually compiles - and all
/// ten READMEs must agree (only the prose is translated). A doc edit that is not mirrored here
/// fails here; a sample edit that no longer compiles fails the build.
/// </summary>
public sealed class ReadmeSamplesTests
{
    private static readonly string[] Readmes =
    [
        "README.md",
        "README.zh.md",
        "README.zh-TW.md",
        "README.de.md",
        "README.es.md",
        "README.fr.md",
        "README.ja.md",
        "README.ko.md",
        "README.pt-BR.md",
        "README.ru.md",
    ];

    [Test]
    public async Task Policy_sample_matches_every_README()
    {
        var mismatches = Compare("readme:policy", "RateLimitPolicy.Create");
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Bucket_sample_matches_every_README()
    {
        var mismatches = Compare("readme:bucket", "RateLimitMiddleware.Create");
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    /// <summary>Returns the READMEs whose block differs from the region (empty when in sync).</summary>
    private static List<string> Compare(string region, string marker)
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "tests", "PicoNode.Docs.Tests", "ReadmeSamples.cs")
        );
        var expected = Normalize(ExtractRegion(source, region));

        var mismatches = new List<string>();
        foreach (var readme in Readmes)
        {
            var blocks = ExtractCsharpBlocks(File.ReadAllText(Path.Combine(root, readme)))
                .Where(block => block.Contains(marker, StringComparison.Ordinal))
                .ToList();

            if (blocks.Count != 1 || Normalize(blocks[0]) != expected)
                mismatches.Add($"{readme} ({blocks.Count} block(s) containing '{marker}')");
        }

        return mismatches;
    }

    private static string ExtractRegion(string source, string region)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == $"// #region {region}");
        if (start < 0)
            throw new InvalidOperationException($"// #region {region} not found in ReadmeSamples.cs");

        var end = Array.FindIndex(lines, start + 1, line => line.Trim() == "// #endregion");
        if (end < 0)
            throw new InvalidOperationException($"// #region {region} is never closed");

        return string.Join('\n', lines[(start + 1)..end]);
    }

    private static IEnumerable<string> ExtractCsharpBlocks(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "```csharp")
                continue;

            var end = Array.FindIndex(lines, i + 1, line => line.Trim() == "```");
            if (end < 0)
                break;

            yield return string.Join('\n', lines[(i + 1)..end]);
            i = end;
        }
    }

    /// <summary>
    /// Drops trailing spaces, blank edges and the common indent, so a sample can be indented
    /// inside a method here while the README keeps it at column 0.
    /// </summary>
    private static string Normalize(string block)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var indent = lines.Where(line => line.Trim().Length > 0)
            .Min(line => line.Length - line.TrimStart().Length);

        return string.Join('\n', lines.Select(line => line.Length >= indent ? line[indent..] : line));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PicoNode.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "PicoNode.slnx not found above the test output directory"
            );
    }
}
