namespace PicoNode.Agent.Tests.BuiltIn;

public class LsToolTests
{
    [Test]
    public async Task LsCurrentDirectory_ReturnsEntries()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "pico_ls_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        File.WriteAllText(Path.Combine(tmpDir, "a.txt"), "");
        File.WriteAllText(Path.Combine(tmpDir, "b.txt"), "");
        Directory.CreateDirectory(Path.Combine(tmpDir, "sub"));

        try
        {
            var tool = new LsTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["path"] = tmpDir },
                Directory.GetCurrentDirectory(),
                CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Content).Contains("a.txt");
            await Assert.That(result.Content).Contains("b.txt");
            await Assert.That(result.Content).Contains("sub/");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Test]
    public async Task LsNonexistentPath_ReturnsError()
    {
        var tool = new LsTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = "/nonexistent/ls_test" },
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("not found");
    }

    [Test]
    public async Task LsDefaultPath_UsesWorkingDirectory()
    {
        var tool = new LsTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>(),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Content.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task LsWithLimit_TruncatesResults()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "pico_ls_limit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        for (int i = 0; i < 10; i++) File.WriteAllText(Path.Combine(tmpDir, $"f{i}.txt"), "");

        try
        {
            var tool = new LsTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["path"] = tmpDir, ["limit"] = 3L },
                Directory.GetCurrentDirectory(),
                CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            var lines = result.Content.Split('\n');
            await Assert.That(lines.Length).IsLessThanOrEqualTo(3);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
