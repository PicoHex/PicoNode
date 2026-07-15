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
