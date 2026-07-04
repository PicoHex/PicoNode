namespace PicoNode.Agent.Tests.BuiltIn;

public class ReadToolTests
{
    [Test]
    public async Task ReadExistingFile_ReturnsContent()
    {
        var tmp = Path.GetTempFileName();
        await File.WriteAllTextAsync(tmp, "hello\nworld");

        var tool = new ReadTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = tmp },
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Content).Contains("hello");
        await Assert.That(result.Content).Contains("world");

        File.Delete(tmp);
    }

    [Test]
    public async Task ReadNonexistentFile_ReturnsError()
    {
        var tool = new ReadTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = "/nonexistent/file.txt" },
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("File not found");
    }

    [Test]
    public async Task ReadWithOffset_ReturnsSubset()
    {
        var tmp = Path.GetTempFileName();
        await File.WriteAllTextAsync(tmp, "line1\nline2\nline3\nline4");

        var tool = new ReadTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = tmp, ["offset"] = 2L, ["limit"] = 2L },
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Content).Contains("line2");
        await Assert.That(result.Content).Contains("line3");
        await Assert.That(result.Content).DoesNotContain("line1");
        await Assert.That(result.Content).DoesNotContain("line4");

        File.Delete(tmp);
    }

    [Test]
    public async Task ReadBinaryFile_ReturnsBinaryMessage()
    {
        var tmp = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tmp, new byte[] { 0x00, 0x01, 0x02, 0x00 });

        var tool = new ReadTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = tmp },
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("Binary file");

        File.Delete(tmp);
    }

    [Test]
    public async Task ReadWithoutPath_ReturnsError()
    {
        var tool = new ReadTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>(),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
    }
}
