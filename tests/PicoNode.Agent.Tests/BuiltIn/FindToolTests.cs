namespace PicoNode.Agent.Tests.BuiltIn;

public class FindToolTests
{
    [Test]
    public async Task FindByGlob_ReturnsMatchingFiles()
    {
        var tmpDir = Path.Combine(
            Path.GetTempPath(),
            "pico_find_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmpDir);
        File.WriteAllText(Path.Combine(tmpDir, "a.cs"), "");
        File.WriteAllText(Path.Combine(tmpDir, "b.cs"), "");
        File.WriteAllText(Path.Combine(tmpDir, "c.txt"), "");
        Directory.CreateDirectory(Path.Combine(tmpDir, "sub"));
        File.WriteAllText(Path.Combine(tmpDir, "sub", "d.cs"), "");

        try
        {
            var tool = new FindTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["pattern"] = "*.cs", ["path"] = tmpDir },
                Directory.GetCurrentDirectory(),
                CancellationToken.None
            );

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Content).Contains("a.cs");
            await Assert.That(result.Content).Contains("b.cs");
            await Assert.That(result.Content).Contains(Path.Combine("sub", "d.cs"));
            await Assert.That(result.Content).DoesNotContain("c.txt");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Test]
    public async Task FindNonexistentPath_ReturnsError()
    {
        var tool = new FindTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["pattern"] = "*.txt", ["path"] = "/nonexistent" },
            Directory.GetCurrentDirectory(),
            CancellationToken.None
        );

        await Assert.That(result.IsError).IsTrue();
    }

    [Test]
    public async Task FindWithoutPattern_ReturnsError()
    {
        var tool = new FindTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>(),
            Directory.GetCurrentDirectory(),
            CancellationToken.None
        );

        await Assert.That(result.IsError).IsTrue();
    }

    [Test]
    public async Task FindWithLimit_TruncatesResults()
    {
        var tmpDir = Path.Combine(
            Path.GetTempPath(),
            "pico_find_limit_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmpDir);
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(tmpDir, $"f{i}.txt"), "");

        try
        {
            var tool = new FindTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?>
                {
                    ["pattern"] = "*.txt",
                    ["path"] = tmpDir,
                    ["limit"] = 3L,
                },
                Directory.GetCurrentDirectory(),
                CancellationToken.None
            );

            await Assert.That(result.IsError).IsFalse();
            var lines = result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Length).IsLessThanOrEqualTo(3);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
