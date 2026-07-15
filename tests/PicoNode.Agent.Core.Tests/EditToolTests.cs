namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: edit tool — replaces oldText with newText in a file using edits[] array.
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

    private static Func<Dictionary<string, object?>, CancellationToken, Task<string>> Handler =>
        EditTool.Create(Directory.GetCurrentDirectory());

    [Test]
    public async Task Edit_ReplacesSingleBlock()
    {
        var path = FilePath("test.txt");
        await File.WriteAllTextAsync(path, "hello world\n");

        var result = await Handler(
            new()
            {
                ["path"] = path,
                ["edits"] = new[]
                {
                    new Dictionary<string, object?> { ["oldText"] = "hello", ["newText"] = "hi" },
                },
            },
            CancellationToken.None
        );

        await Assert.That(result).Contains("1 edit");
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("hi world\n");
    }

    [Test]
    public async Task Edit_OldTextNotFound_ReturnsError()
    {
        var path = FilePath("test.txt");
        await File.WriteAllTextAsync(path, "hello\n");

        var result = await Handler(
            new()
            {
                ["path"] = path,
                ["edits"] = new[]
                {
                    new Dictionary<string, object?> { ["oldText"] = "xyz", ["newText"] = "abc" },
                },
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

        var result = await Handler(
            new()
            {
                ["path"] = path,
                ["edits"] = new[]
                {
                    new Dictionary<string, object?> { ["oldText"] = "hello", ["newText"] = "hi" },
                },
            },
            CancellationToken.None
        );

        await Assert.That(result).Contains("[Error");
        await Assert.That(result).Contains("not unique");
    }

    [Test]
    public async Task Edit_FileNotFound_ReturnsError()
    {
        var result = await Handler(
            new()
            {
                ["path"] = "/nonexistent",
                ["edits"] = new[]
                {
                    new Dictionary<string, object?> { ["oldText"] = "a", ["newText"] = "b" },
                },
            },
            CancellationToken.None
        );

        await Assert.That(result).Contains("[Error");
        await Assert.That(result).Contains("File not found");
    }
}
