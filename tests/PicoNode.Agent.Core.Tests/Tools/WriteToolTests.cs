namespace PicoNode.Agent.Core.Tests.Tools;

public sealed class WriteToolTests
{
    [Test]
    public async Task Create_ReturnsHandlerThatWritesFile()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-write-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        try
        {
            var handler = WriteTool.Create(tmp);
            var result = await handler(
                new Dictionary<string, object?> { ["path"] = "test.txt", ["content"] = "hello" },
                CancellationToken.None
            );
            await Assert.That(result).Contains("Wrote");
            await Assert.That(File.Exists(Path.Combine(tmp, "test.txt"))).IsTrue();
            await Assert
                .That(await File.ReadAllTextAsync(Path.Combine(tmp, "test.txt")))
                .IsEqualTo("hello");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_CreatesParentDirectories()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-write-" + Guid.NewGuid().ToString("N")[..8]
        );
        try
        {
            var handler = WriteTool.Create(tmp);
            await handler(
                new Dictionary<string, object?> { ["path"] = "sub/deep/f.txt", ["content"] = "x" },
                CancellationToken.None
            );
            await Assert.That(File.Exists(Path.Combine(tmp, "sub/deep/f.txt"))).IsTrue();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
