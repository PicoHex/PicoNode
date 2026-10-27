namespace PicoNode.Docs.Tests;

/// <summary>
/// One reading of "what a README contains", shared by every rule that measures the docs: the
/// ten <c>README*.md</c> files are discovered on disk (never listed by hand, so a new translation
/// cannot be silently exempt), every fenced block is parsed once, and the compiled samples are
/// discovered by walking the sample sources for <c>// #region readme:*</c> markers (never listed
/// by hand, so a new region cannot be silently unchecked).
/// </summary>
internal static class ReadmeCorpus
{
    /// <summary>The repository root (the directory holding <c>PicoNode.slnx</c>).</summary>
    internal static string Root { get; } =
        FindRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException(
            "PicoNode.slnx not found above the test output directory"
        );

    /// <summary>
    /// The ten translated READMEs at the repository root, discovered by glob. Ordered, so
    /// failure messages are stable and <c>Readmes[0]</c> is <c>README.md</c> (English).
    /// </summary>
    internal static string[] Readmes { get; } =
        Directory
            .GetFiles(Root, "README*.md", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The English README, the source of truth every translation is compared with.</summary>
    internal static string EnglishReadme => Path.Combine(Root, "README.md");

    private static string? FindRoot(string start)
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PicoNode.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Every fence of a markdown file, in document order, paired opener-to-closer the way
    /// CommonMark pairs them (the first fence opens, the second closes, and so on). A closing
    /// fence must carry no info string, and that asymmetry is what makes an <em>unbalanced</em>
    /// fence visible: a stray opener is read as an opener, the next real opener becomes its
    /// closer, and its info string is then non-empty (see <see cref="Fence.CloserInfo"/>).
    /// </summary>
    internal static List<Fence> Fences(string markdown)
    {
        var lines = SplitLines(markdown);
        var markers = new List<(int Line, string Info)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                markers.Add((i, trimmed[3..].Trim()));
        }

        var fences = new List<Fence>();

        for (var k = 0; k < markers.Count; k += 2)
        {
            var (openLine, info) = markers[k];
            var isPaired = k + 1 < markers.Count;
            var closer = isPaired ? markers[k + 1] : ((int Line, string Info)?)null;
            var bodyEnd = closer?.Line ?? lines.Length;

            fences.Add(
                new Fence(
                    openLine + 1,
                    info,
                    string.Join('\n', lines[(openLine + 1)..bodyEnd]),
                    closer?.Info ?? "",
                    Closed: isPaired
                )
            );
        }

        return fences;
    }

    /// <summary>The <c>csharp</c> fences of a markdown file, in document order.</summary>
    internal static List<Fence> CsharpBlocks(string markdown) =>
        Fences(markdown).Where(static fence => fence.Info == "csharp").ToList();

    /// <summary>
    /// The compiled README samples: every <c>// #region readme:&lt;name&gt;</c> in the sample
    /// sources, as name → normalized body. Walking the sources (instead of listing region names)
    /// means a region the docs stopped using, or a new one nobody mirrored, shows up as a
    /// disagreement rather than as a rule that quietly checks nothing.
    /// </summary>
    internal static Dictionary<string, string> Regions()
    {
        var regions = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in SampleSources())
        {
            var lines = SplitLines(File.ReadAllText(path));

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                const string marker = "// #region readme:";

                if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
                    continue;

                var name = trimmed[marker.Length..].Trim();
                var end = Array.FindIndex(
                    lines,
                    i + 1,
                    static line => line.Trim() == "// #endregion"
                );

                if (end < 0)
                    throw new InvalidOperationException(
                        $"// #region readme:{name} is never closed in {path}"
                    );

                var body = Normalize(string.Join('\n', lines[(i + 1)..end]));

                if (!regions.TryAdd(name, body))
                    throw new InvalidOperationException($"// #region readme:{name} is declared twice");

                i = end;
            }
        }

        return regions;
    }

    /// <summary>The <c>*.cs</c> files that may hold a <c>readme:</c> region.</summary>
    internal static IEnumerable<string> SampleSources()
    {
        string[] directories =
        [
            Path.Combine(Root, "samples", "readme"),
            Path.Combine(Root, "tests", "PicoNode.Docs.Tests"),
        ];

        return directories
            .Where(Directory.Exists)
            .SelectMany(directory =>
                Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            )
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            )
            .Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Drops trailing spaces, blank edges and the common indent, so a sample can be indented
    /// inside a method here while the README keeps it at column 0.
    /// </summary>
    internal static string Normalize(string block)
    {
        var lines = SplitLines(block).Select(static line => line.TrimEnd()).ToList();

        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count == 0)
            return "";

        var indent = lines
            .Where(static line => line.Trim().Length > 0)
            .Min(static line => line.Length - line.TrimStart().Length);

        return string.Join(
            '\n',
            lines.Select(line => line.Length >= indent ? line[indent..] : line)
        );
    }

    /// <summary>The first non-blank line of a block, trimmed — what a failure message quotes.</summary>
    internal static string FirstLine(string block) =>
        SplitLines(block).FirstOrDefault(static line => line.Trim().Length > 0)?.Trim()
        ?? "<empty block>";

    internal static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}

/// <summary>A fenced markdown block: the opener's line and info string, the body, and the closer.</summary>
/// <param name="Line">1-based line of the opening fence.</param>
/// <param name="Info">The opening fence's info string (e.g. <c>csharp</c>); empty for a bare fence.</param>
/// <param name="Body">The lines between the fences.</param>
/// <param name="CloserInfo">
/// The closing fence's info string. Empty for a well-formed pair, non-empty when a stray opener
/// made a real opener play the closer.
/// </param>
/// <param name="Closed">False when the opener was never closed (an odd number of fences).</param>
internal sealed record Fence(int Line, string Info, string Body, string CloserInfo, bool Closed);
