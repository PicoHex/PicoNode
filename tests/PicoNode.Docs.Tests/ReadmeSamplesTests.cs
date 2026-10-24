namespace PicoNode.Docs.Tests;

/// <summary>
/// Anti-drift check for the README code samples: every README's block must equal the marked
/// region of the sample it documents — the copy that actually compiles — and all ten READMEs must
/// agree (only the prose is translated). A doc edit that is not mirrored in the sample fails here;
/// a sample that stops compiling fails the build, because this project references the sample
/// project that compiles <c>samples/readme/*.cs</c>. Which files are read, and which regions
/// exist, is discovered by <see cref="ReadmeCorpus"/> rather than listed here.
/// </summary>
public sealed class ReadmeSamplesTests
{
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

    [Test]
    public async Task Echo_sample_matches_every_README()
    {
        var mismatches = Compare(
            "readme:echo",
            "EchoHandler : ITcpConnectionHandler",
            "EchoSample.cs"
        );
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Metrics_sample_matches_every_README()
    {
        var mismatches = Compare(
            "readme:metrics",
            "udpMetrics.TotalDatagramsSent",
            "MetricsSample.cs"
        );
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Options_sample_matches_every_README()
    {
        var mismatches = Compare("readme:options", "MaxConnections = 500", "ServerSamples.cs");
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Reload_sample_matches_every_README()
    {
        var mismatches = Compare(
            "readme:reload",
            "ICfgRoot for live reload",
            "ServerSamples.cs"
        );
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Logging_sample_matches_every_README()
    {
        var mismatches = Compare("readme:logging", "All transport faults logged here", "ServerSamples.cs");
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Compression_sample_matches_every_README()
    {
        var mismatches = Compare(
            "readme:compression",
            "new CompressionMiddleware(",
            "ServerSamples.cs"
        );
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Static_files_sample_matches_every_README()
    {
        var mismatches = Compare(
            "readme:files",
            "new StaticFileMiddleware(",
            "ServerSamples.cs"
        );
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Cors_sample_matches_every_README()
    {
        var mismatches = Compare("readme:cors", "CorsHandler.HandlePreflight", "ServerSamples.cs");
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    [Test]
    public async Task Cookies_sample_matches_every_README()
    {
        var mismatches = Compare(
            "readme:cookies",
            "CookieParser.Parse(context.Request.HeaderFields)",
            "ServerSamples.cs"
        );
        await Assert.That(string.Join("; ", mismatches)).IsEqualTo("");
    }

    /// <summary>
    /// Returns the READMEs whose block differs from the region (empty when in sync).
    /// <paramref name="sampleFile"/> resolves under <c>samples/readme/</c>; null means the region
    /// lives in <c>ReadmeSamples.cs</c> beside this test.
    /// </summary>
    private static List<string> Compare(string region, string marker, string? sampleFile = null)
    {
        var samplePath = sampleFile is null
            ? Path.Combine(ReadmeCorpus.Root, "tests", "PicoNode.Docs.Tests", "ReadmeSamples.cs")
            : Path.Combine(ReadmeCorpus.Root, "samples", "readme", sampleFile);
        var expected = ReadmeCorpus.Normalize(
            ExtractRegion(File.ReadAllText(samplePath), region, samplePath)
        );

        var mismatches = new List<string>();
        foreach (var readme in ReadmeCorpus.Readmes)
        {
            var blocks = ReadmeCorpus
                .CsharpBlocks(File.ReadAllText(readme))
                .Where(block => block.Body.Contains(marker, StringComparison.Ordinal))
                .ToList();

            if (blocks.Count != 1 || ReadmeCorpus.Normalize(blocks[0].Body) != expected)
            {
                mismatches.Add(
                    $"{Path.GetFileName(readme)} ({blocks.Count} block(s) containing '{marker}')"
                );
            }
        }

        return mismatches;
    }

    private static string ExtractRegion(string source, string region, string path)
    {
        var lines = ReadmeCorpus.SplitLines(source);
        var start = Array.FindIndex(lines, line => line.Trim() == $"// #region {region}");
        if (start < 0)
            throw new InvalidOperationException($"// #region {region} not found in {path}");

        var end = Array.FindIndex(lines, start + 1, line => line.Trim() == "// #endregion");
        if (end < 0)
            throw new InvalidOperationException($"// #region {region} is never closed in {path}");

        return string.Join('\n', lines[(start + 1)..end]);
    }
}
