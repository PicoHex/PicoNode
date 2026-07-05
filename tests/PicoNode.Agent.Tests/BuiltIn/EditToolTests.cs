namespace PicoNode.Agent.Tests.BuiltIn;

public class EditToolTests
{
    [Test]
    public async Task EditSingleReplacement_WritesFile()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "hello world");

        try
        {
            var tool = new EditTool();
            var args = new Dictionary<string, object?>
            {
                ["path"] = tmp,
                ["edits"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["oldText"] = "hello", ["newText"] = "hi" }
                }
            };
            var result = await tool.ExecuteAsync(args, Directory.GetCurrentDirectory(), CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.ReadAllText(tmp)).IsEqualTo("hi world");
        }
        finally { File.Delete(tmp); }
    }

    [Test]
    public async Task EditMultipleReplacements_AppliesAll()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "aaa bbb ccc");

        try
        {
            var tool = new EditTool();
            var args = new Dictionary<string, object?>
            {
                ["path"] = tmp,
                ["edits"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["oldText"] = "aaa", ["newText"] = "111" },
                    new Dictionary<string, object?> { ["oldText"] = "ccc", ["newText"] = "333" },
                }
            };
            var result = await tool.ExecuteAsync(args, Directory.GetCurrentDirectory(), CancellationToken.None);

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.ReadAllText(tmp)).IsEqualTo("111 bbb 333");
        }
        finally { File.Delete(tmp); }
    }

    [Test]
    public async Task EditOldTextNotFound_ReturnsError()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "hello world");

        try
        {
            var tool = new EditTool();
            var args = new Dictionary<string, object?>
            {
                ["path"] = tmp,
                ["edits"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["oldText"] = "nonexistent", ["newText"] = "x" }
                }
            };
            var result = await tool.ExecuteAsync(args, Directory.GetCurrentDirectory(), CancellationToken.None);

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Content).Contains("not found");
            // File unchanged
            await Assert.That(File.ReadAllText(tmp)).IsEqualTo("hello world");
        }
        finally { File.Delete(tmp); }
    }

    [Test]
    public async Task EditDuplicateOldText_ReturnsError()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "hello hello");

        try
        {
            var tool = new EditTool();
            var args = new Dictionary<string, object?>
            {
                ["path"] = tmp,
                ["edits"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["oldText"] = "hello", ["newText"] = "hi" }
                }
            };
            var result = await tool.ExecuteAsync(args, Directory.GetCurrentDirectory(), CancellationToken.None);

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Content).Contains("multiple");
        }
        finally { File.Delete(tmp); }
    }

    [Test]
    public async Task EditMissingPath_ReturnsError()
    {
        var tool = new EditTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["edits"] = new List<object?>() },
            Directory.GetCurrentDirectory(), CancellationToken.None);

        await Assert.That(result.IsError).IsTrue();
    }
}
