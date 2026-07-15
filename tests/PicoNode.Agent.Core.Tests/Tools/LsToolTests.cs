namespace PicoNode.Agent.Core.Tests.Tools;

public sealed class LsToolTests
{
    [Test]
    public async Task Create_ListsDirectory()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pico-ls-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "a.txt"), "");
        File.WriteAllText(Path.Combine(tmp, "b.txt"), "");
        try
        {
            var handler = LsTool.Create(tmp);
            var result = await handler(new Dictionary<string, object?>(), CancellationToken.None);
            await Assert.That(result).Contains("a.txt");
            await Assert.That(result).Contains("b.txt");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_RespectsLimit()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pico-ls-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(tmp, $"{i}.txt"), "");
        try
        {
            var handler = LsTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["limit"] = 3 },
                CancellationToken.None
            );
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Length).IsEqualTo(4); // 3 files + truncation note
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
