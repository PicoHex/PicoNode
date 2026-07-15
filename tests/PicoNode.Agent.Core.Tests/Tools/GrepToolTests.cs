namespace PicoNode.Agent.Core.Tests.Tools;

public sealed class GrepToolTests
{
    [Test]
    public async Task Create_BasicMatch()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-grep-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "a.txt"), "hello world\nfoo bar\nhello again");
        try
        {
            var handler = GrepTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["pattern"] = "hello" },
                CancellationToken.None
            );
            await Assert.That(result).Contains("hello world");
            await Assert.That(result).Contains("hello again");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_GlobFilter()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-grep-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "a.cs"), "hello");
        File.WriteAllText(Path.Combine(tmp, "b.txt"), "hello");
        try
        {
            var handler = GrepTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["pattern"] = "hello", ["glob"] = "*.cs" },
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
    public async Task Create_CaseInsensitive()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-grep-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "a.txt"), "HELLO");
        try
        {
            var handler = GrepTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["pattern"] = "hello", ["ignoreCase"] = true },
                CancellationToken.None
            );
            await Assert.That(result).Contains("a.txt:1");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
