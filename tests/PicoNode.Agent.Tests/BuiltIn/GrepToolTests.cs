namespace PicoNode.Agent.Tests.BuiltIn;

public class GrepToolTests
{
    [Test]
    public async Task GrepFindsMatchingLines()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "pico_grep_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "hello world\nfoo bar\nhello again");

        try
        {
            var tool = new GrepTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["pattern"] = "hello", ["path"] = tmpDir },
                Directory.GetCurrentDirectory(),
                CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Content).Contains("hello world");
            await Assert.That(result.Content).Contains("hello again");
            await Assert.That(result.Content).Contains("a.txt");
            await Assert.That(result.Content).DoesNotContain("foo bar");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Test]
    public async Task GrepWithGlob_FiltersFiles()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "pico_grep_glob_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        File.WriteAllText(Path.Combine(tmpDir, "a.cs"), "hello");
        File.WriteAllText(Path.Combine(tmpDir, "b.txt"), "hello");

        try
        {
            var tool = new GrepTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["pattern"] = "hello", ["path"] = tmpDir, ["include"] = "*.cs" },
                Directory.GetCurrentDirectory(),
                CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Content).Contains("a.cs");
            await Assert.That(result.Content).DoesNotContain("b.txt");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Test]
    public async Task GrepWithoutPattern_ReturnsError()
    {
        var tool = new GrepTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>(),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
    }

    [Test]
    public async Task GrepNonexistentPath_ReturnsError()
    {
        var tool = new GrepTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["pattern"] = "x", ["path"] = "/nonexistent" },
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
    }
}
