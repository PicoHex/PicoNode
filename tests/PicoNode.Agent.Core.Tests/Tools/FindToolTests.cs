namespace PicoNode.Agent.Core.Tests.Tools;

public sealed class FindToolTests
{
    [Test]
    public async Task Create_GlobMatch()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-find-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "a.cs"), "");
        File.WriteAllText(Path.Combine(tmp, "b.txt"), "");
        try
        {
            var handler = FindTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["pattern"] = "*.cs" },
                CancellationToken.None
            );
            await Assert.That(result).Contains("a.cs");
            await Assert.That(result).DoesNotContain("b.txt");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_RespectsLimit()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-find-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(tmp, $"{i}.txt"), "");
        try
        {
            var handler = FindTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["pattern"] = "*.txt", ["limit"] = 3 },
                CancellationToken.None
            );
            await Assert
                .That(result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)
                .IsEqualTo(3);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
