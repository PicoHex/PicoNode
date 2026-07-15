using System.Text;

namespace PicoNode.Agent.Core.Tests.Tools;

public sealed class ReadToolTests
{
    [Test]
    public async Task Create_ReadsTextFile()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-read-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "f.txt"), "line1\nline2\nline3");
        try
        {
            var handler = ReadTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["path"] = "f.txt" },
                CancellationToken.None
            );
            await Assert.That(result).Contains("line1");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_OffsetAndLimit()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-read-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "f.txt"), "a\nb\nc\nd\ne");
        try
        {
            var handler = ReadTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?>
                {
                    ["path"] = "f.txt",
                    ["offset"] = 2,
                    ["limit"] = 2,
                },
                CancellationToken.None
            );
            await Assert.That(result).Contains("b");
            await Assert.That(result).Contains("c");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_TruncatesAtUtf8Boundary()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-read-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        // Create content where the char-level cut (51200) falls in the middle
        // of a surrogate pair. 50001 ASCII + 1500 emoji = 53001 chars, 56001 bytes.
        // Emoji #600: high surrogate at pos 51199, low at 51200.
        // output[..51200] orphans the high surrogate if sliced by char index.
        var ascii = new string('x', 50001);
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 1500));
        var content = ascii + emoji;
        File.WriteAllText(Path.Combine(tmp, "f.txt"), content, Encoding.UTF8);
        try
        {
            var handler = ReadTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["path"] = "f.txt" },
                CancellationToken.None
            );
            await Assert.That(result).Contains("Truncated");
            var truncatedText = result.Split("\n\n[")[0];
            // Must not end with orphaned surrogate
            if (truncatedText.Length > 0)
            {
                await Assert.That(char.IsHighSurrogate(truncatedText[^1])).IsFalse();
                await Assert.That(char.IsLowSurrogate(truncatedText[^1])).IsFalse();
            }
            // UTF-8 round-trip must be lossless
            var reEncoded = Encoding.UTF8.GetBytes(truncatedText);
            var reDecoded = Encoding.UTF8.GetString(reEncoded);
            await Assert.That(reDecoded).IsEqualTo(truncatedText);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_ImageFile_ReturnsNote()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-read-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
        File.WriteAllBytes(Path.Combine(tmp, "img.png"), png);
        try
        {
            var handler = ReadTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["path"] = "img.png" },
                CancellationToken.None
            );
            await Assert.That(result).Contains("Image file");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
