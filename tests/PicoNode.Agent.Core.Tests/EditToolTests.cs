namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: edit tool — replaces oldText with newText in a file.
/// </summary>
public sealed class EditToolTests
{
    private string _tmp = null!;

    [Before(Test)]
    public void Setup()
    {
        _tmp = Path.Combine(
            Path.GetTempPath(),
            "picoagent-edit-test-" + Guid.NewGuid().ToString("N")[..8]
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

    [Test]
    public async Task Edit_ReplacesSingleBlock()
    {
        var path = FilePath("test.txt");
        await File.WriteAllTextAsync(path, "hello world\n");

        var result = await ToolHandlers.EditAsync(
            new()
            {
                ["path"] = path,
                ["oldText"] = "hello",
                ["newText"] = "hi",
            },
            CancellationToken.None
        );

        await Assert.That(result).Contains("Replaced");
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("hi world\n");
    }

    [Test]
    public async Task Edit_OldTextNotFound_ReturnsError()
    {
        var path = FilePath("test.txt");
        await File.WriteAllTextAsync(path, "hello\n");

        var result = await ToolHandlers.EditAsync(
            new()
            {
                ["path"] = path,
                ["oldText"] = "xyz",
                ["newText"] = "abc",
            },
            CancellationToken.None
        );

        await Assert.That(result).Contains("[Error");
        await Assert.That(result).Contains("not found");
    }

    [Test]
    public async Task Edit_DuplicateOldText_ReturnsError()
    {
        var path = FilePath("test.txt");
        await File.WriteAllTextAsync(path, "hello\nhello\n");

        var result = await ToolHandlers.EditAsync(
            new()
            {
                ["path"] = path,
                ["oldText"] = "hello",
                ["newText"] = "hi",
            },
            CancellationToken.None
        );

        await Assert.That(result).Contains("[Error");
        await Assert.That(result).Contains("2 locations");
    }

    [Test]
    public async Task Edit_FileNotFound_ReturnsError()
    {
        var result = await ToolHandlers.EditAsync(
            new()
            {
                ["path"] = "/nonexistent",
                ["oldText"] = "a",
                ["newText"] = "b",
            },
            CancellationToken.None
        );

        await Assert.That(result).Contains("[Error");
        await Assert.That(result).Contains("File not found");
    }

    [Test]
    public async Task Edit_PreservesCrlfLineEndings()
    {
        var path = FilePath("crlf.txt");
        await File.WriteAllTextAsync(path, "line1\r\nline2\r\n");

        await ToolHandlers.EditAsync(
            new()
            {
                ["path"] = path,
                ["oldText"] = "line1",
                ["newText"] = "LINE1",
            },
            CancellationToken.None
        );

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("LINE1\r\nline2\r\n");
    }

    [Test]
    public async Task Edit_PreservesLfLineEndings()
    {
        var path = FilePath("lf.txt");
        await File.WriteAllTextAsync(path, "line1\nline2\n");

        await ToolHandlers.EditAsync(
            new()
            {
                ["path"] = path,
                ["oldText"] = "line1",
                ["newText"] = "LINE1",
            },
            CancellationToken.None
        );

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("LINE1\nline2\n");
    }
}
