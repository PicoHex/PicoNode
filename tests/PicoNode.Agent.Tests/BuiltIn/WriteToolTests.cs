namespace PicoNode.Agent.Tests.BuiltIn;

public class WriteToolTests
{
    [Test]
    public async Task WriteFile_CreatesFile()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "pico_write_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var filePath = Path.Combine(tmpDir, "test.txt");
            var tool = new WriteTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["path"] = filePath, ["content"] = "hello world" },
                Directory.GetCurrentDirectory(),
                CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.Exists(filePath)).IsTrue();
            await Assert.That(File.ReadAllText(filePath)).IsEqualTo("hello world");
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Test]
    public async Task WriteFile_CreatesParentDirectories()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "pico_wp_" + Guid.NewGuid().ToString("N")[..8]);
        var nested = Path.Combine(tmpDir, "a", "b", "test.txt");
        try
        {
            var tool = new WriteTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["path"] = nested, ["content"] = "nested" },
                Directory.GetCurrentDirectory(),
                CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.Exists(nested)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Test]
    public async Task WriteFile_OverwritesExistingFile()
    {
        var tmp = Path.GetTempFileName();
        await File.WriteAllTextAsync(tmp, "old");
        try
        {
            var tool = new WriteTool();
            var result = await tool.ExecuteAsync(
                new Dictionary<string, object?> { ["path"] = tmp, ["content"] = "new" },
                Directory.GetCurrentDirectory(),
                CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.ReadAllText(tmp)).IsEqualTo("new");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Test]
    public async Task WriteFile_MissingPath_ReturnsError()
    {
        var tool = new WriteTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["content"] = "x" },
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("path is required");
    }
}
