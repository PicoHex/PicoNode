namespace PicoNode.Docs.Tests;

/// <summary>
/// The code blocks of the ten READMEs, measured instead of trusted: a fence that does not close,
/// a block that one translation changed and the others did not, a block that no compile ever
/// touches, and a compiled sample nobody quotes are all failures here — each one is a claim about
/// code that nothing was checking.
/// <para>
/// The convention these rules enforce is the one <see cref="ReadmeSamplesTests"/> already relied
/// on: all ten READMEs share one code block per sample (only the prose is translated), and a
/// sample is either a compiled region or an explicit, reasoned fragment.
/// </para>
/// </summary>
public sealed class ReadmeCodeBlockTests
{
    /// <summary>
    /// The marker a deliberately-uncompiled block must carry on the line above its fence:
    /// <c>&lt;!-- readme-fragment: why this cannot be a compiled sample --&gt;</c>. The reason is
    /// mandatory and must say something — "TODO" is not a reason.
    /// </summary>
    private const string FragmentMarker = "<!-- readme-fragment:";

    /// <summary>
    /// Floor for the compiled blocks, measured on the tree that introduced this rule (11 of 20
    /// blocks: the 4 pre-existing regions plus 7 samples compiled by this change — options,
    /// reload, logging, compression, static files, CORS, cookies &amp; multipart). It is a ratchet,
    /// not a restatement of a doc value: raise it when you compile another block, never lower it,
    /// so "mark it illustrative and move on" cannot quietly become the only strategy.
    /// </summary>
    private const int CompiledBlocksFloor = 11;

    [Test]
    public async Task Every_readme_has_balanced_code_fences()
    {
        // An unbalanced fence is not cosmetic: the stray opener at README.md:448 turned every
        // heading and paragraph after it into literal code in the rendered page, for the English
        // README only. A translation-parity check would not have caught it on its own; the fence
        // count does.
        var unreadable = new List<string>();

        foreach (var path in ReadmeCorpus.Readmes)
        {
            var fences = ReadmeCorpus.Fences(File.ReadAllText(path));

            foreach (var fence in fences)
            {
                var name = Path.GetFileName(path);

                if (!fence.Closed)
                {
                    unreadable.Add($"{name}:{fence.Line}: fence '```{fence.Info}' is never closed");
                }
                else if (fence.CloserInfo.Length != 0)
                {
                    unreadable.Add(
                        $"{name}:{fence.Line}: fence '```{fence.Info}' is closed by '```{fence.CloserInfo}' "
                            + "— a stray fence above it produced this pairing"
                    );
                }
            }
        }

        await Assert
            .That(string.Join(" | ", unreadable))
            .IsEqualTo("")
            .Because("every ``` fence must pair with a bare closing fence");
    }

    [Test]
    public async Task Every_readme_block_matches_the_english_readme()
    {
        // The ten READMEs advertise the same API in the same snippets; only the prose is
        // translated (tests/PicoNode.Docs.Tests/README.md states the convention). Drift here is
        // silent and expensive: seven translations omitted `Config = config` from the reload
        // sample, one kept a `app.Use((ctx, next, ct) => …)` that cannot compile because the body
        // awaits without `async`, and the controller sample said two different things about DI
        // registration depending on the language you read.
        var english = ReadmeCorpus.CsharpBlocks(File.ReadAllText(ReadmeCorpus.EnglishReadme))
            .Select(static block => ReadmeCorpus.Normalize(block.Body))
            .ToArray();

        await Assert
            .That(english.Length)
            .IsGreaterThan(0)
            .Because("the English README is the source of truth, so it must contain code blocks");

        var drift = new List<string>();

        foreach (var path in ReadmeCorpus.Readmes)
        {
            var name = Path.GetFileName(path);

            if (path == ReadmeCorpus.EnglishReadme)
                continue;

            var blocks = ReadmeCorpus.CsharpBlocks(File.ReadAllText(path));

            if (blocks.Count != english.Length)
            {
                drift.Add($"{name}: {blocks.Count} csharp blocks, README.md has {english.Length}");
                continue;
            }

            for (var i = 0; i < blocks.Count; i++)
            {
                if (ReadmeCorpus.Normalize(blocks[i].Body) != english[i])
                {
                    drift.Add(
                        $"{name}:{blocks[i].Line}: block {i + 1} differs — first line "
                            + $"'{ReadmeCorpus.FirstLine(blocks[i].Body)}'"
                    );
                }
            }
        }

        await Assert
            .That(string.Join(" | ", drift))
            .IsEqualTo("")
            .Because("all ten READMEs must carry the same code blocks");
    }

    [Test]
    public async Task Every_readme_code_block_is_compiled_or_marked_illustrative()
    {
        // A block that "looks like code" is a promise that the code works. Either a compiled
        // sample holds it verbatim (then the build proves it), or the README says in writing that
        // it is a fragment — with a reason, so the decision is visible in review instead of
        // absent. This is the rule that would have caught the serialization block T6.2 rewrote:
        // it was inserted as an uncompiled fragment and nothing said so.
        var readmeLines = ReadmeCorpus.SplitLines(File.ReadAllText(ReadmeCorpus.EnglishReadme));
        var blocks = ReadmeCorpus.CsharpBlocks(File.ReadAllText(ReadmeCorpus.EnglishReadme));
        var regions = ReadmeCorpus.Regions();

        var findings = new List<string>();
        var compiled = 0;
        var illustrative = 0;

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var body = ReadmeCorpus.Normalize(block.Body);

            if (regions.Values.Contains(body, StringComparer.Ordinal))
            {
                compiled++;
                continue;
            }

            var reason = FragmentReason(readmeLines, block.Line);

            if (reason is null)
            {
                findings.Add(
                    $"README.md:{block.Line}: block {i + 1} is neither a compiled sample nor marked "
                        + $"'{FragmentMarker} … -->' — first line '{ReadmeCorpus.FirstLine(block.Body)}'"
                );
                continue;
            }

            if (reason.Length < 40)
            {
                findings.Add(
                    $"README.md:{block.Line}: block {i + 1} has a placeholder reason '{reason}' — "
                        + "say what a compiled sample would need"
                );
                continue;
            }

            illustrative++;
        }

        await Assert
            .That(string.Join(" | ", findings))
            .IsEqualTo("")
            .Because("an uncompiled block is a claim nobody checks unless it says so");

        await Assert
            .That(compiled + illustrative)
            .IsEqualTo(blocks.Count)
            .Because("every block is either compiled or explicitly a fragment");

        await Assert
            .That(compiled)
            .IsGreaterThanOrEqualTo(CompiledBlocksFloor)
            .Because(
                $"at least {CompiledBlocksFloor} of the {blocks.Count} README blocks must stay compiled "
                    + "samples — a fragment marker documents a gap, it does not close it"
            );
    }

    [Test]
    public async Task Every_compiled_region_is_quoted_by_the_english_readme()
    {
        // The other direction: a `#region readme:*` that no README block matches is a sample the
        // docs do not show (dead weight that still has to compile), and a region quoted twice
        // means two blocks claim to document the same sample.
        var regions = ReadmeCorpus.Regions();
        var blocks = ReadmeCorpus
            .CsharpBlocks(File.ReadAllText(ReadmeCorpus.EnglishReadme))
            .Select(static block => ReadmeCorpus.Normalize(block.Body))
            .ToArray();

        var unused = regions
            .Where(pair => !blocks.Contains(pair.Value, StringComparer.Ordinal))
            .Select(static pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await Assert
            .That(unused)
            .IsEmpty()
            .Because("a compiled region with no README block behind it is a sample nobody sees");

        var duplicated = blocks
            .Where(body => regions.Values.Count(region => region == body) > 1)
            .Select(ReadmeCorpus.FirstLine)
            .ToArray();

        await Assert
            .That(duplicated)
            .IsEmpty()
            .Because("two blocks quoting the same region means one of them is unguarded");
    }

    /// <summary>
    /// The reason on the fragment marker above <paramref name="fenceLine"/> (1-based), or null
    /// when there is no marker. Blank lines between the marker and the fence are allowed; prose is
    /// not, so the marker cannot be parked at the top of a section.
    /// </summary>
    private static string? FragmentReason(string[] lines, int fenceLine)
    {
        for (var i = fenceLine - 2; i >= 0; i--)
        {
            var line = lines[i].Trim();

            if (line.Length == 0)
                continue;

            if (!line.StartsWith(FragmentMarker, StringComparison.Ordinal))
                return null;

            var end = line.LastIndexOf("-->", StringComparison.Ordinal);

            return end < 0 ? null : line[(FragmentMarker.Length)..end].Trim();
        }

        return null;
    }
}
