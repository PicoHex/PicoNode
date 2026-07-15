namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: grep/find tools — native C# implementations.
/// </summary>
public sealed class GrepFindToolTests
{
    private string _tmp = null!;

    [Before(Test)]
    public void Setup()
    {
        _tmp = Path.Combine(
            Path.GetTempPath(),
            "picoagent-grepfind-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_tmp);
    }

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_tmp))
            Directory.Delete(_tmp, true);
    }

    private string FilePath(string name) => Path.Combine(_tmp, name);

    // ── grep ───────────────────────────────────────────────────────

    [Test]
    public async Task Grep_FindsMatchingLine()
    {
        var path = FilePath("a.txt");
        await File.WriteAllTextAsync(path, "hello world\nfoo bar\n");

        var handler = GrepTool.Create(_tmp);
        var result = await handler(new() { ["pattern"] = "hello" }, CancellationToken.None);

        await Assert.That(result).Contains("hello world");
    }

    [Test]
    public async Task Grep_NoMatch_ReturnsEmptyMessage()
    {
        var path = FilePath("a.txt");
        await File.WriteAllTextAsync(path, "foo\n");

        var handler = GrepTool.Create(_tmp);
        var result = await handler(new() { ["pattern"] = "xyz" }, CancellationToken.None);

        await Assert.That(result).Contains("[No matches]");
    }

    [Test]
    public async Task Grep_DirectoryNotFound_ReturnsError()
    {
        var handler = GrepTool.Create(_tmp);
        var result = await handler(
            new() { ["pattern"] = "x", ["path"] = "/nonexistent" },
            CancellationToken.None
        );

        await Assert.That(result).Contains("[Error");
    }

    // ── find ───────────────────────────────────────────────────────

    [Test]
    public async Task Find_MatchingFile()
    {
        await File.WriteAllTextAsync(FilePath("hello.txt"), "");
        await File.WriteAllTextAsync(FilePath("world.md"), "");

        var handler = FindTool.Create(_tmp);
        var result = await handler(new() { ["pattern"] = "*.txt" }, CancellationToken.None);

        await Assert.That(result).Contains("hello.txt");
        await Assert.That(result).DoesNotContain("world.md");
    }

    [Test]
    public async Task Find_NoMatch_ReturnsEmptyMessage()
    {
        var handler = FindTool.Create(_tmp);
        var result = await handler(new() { ["pattern"] = "nonexistent*" }, CancellationToken.None);

        await Assert.That(result).Contains("[No files found]");
    }

    [Test]
    public async Task Find_DirectoryNotFound_ReturnsError()
    {
        var handler = FindTool.Create(_tmp);
        var result = await handler(
            new() { ["pattern"] = "*.txt", ["path"] = "/nonexistent" },
            CancellationToken.None
        );

        await Assert.That(result).Contains("[Error");
    }
}
