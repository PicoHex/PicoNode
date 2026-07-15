namespace PicoNode.Agent.Core.Tests.Tools;

public sealed class EditToolTests
{
    [Test]
    public async Task Create_SingleEdit()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-edit-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        var file = Path.Combine(tmp, "f.txt");
        File.WriteAllText(file, "hello world");
        try
        {
            var handler = EditTool.Create(tmp);
            var edits = new[]
            {
                new Dictionary<string, object?> { ["oldText"] = "hello", ["newText"] = "hi" },
            };
            var result = await handler(
                new Dictionary<string, object?> { ["path"] = "f.txt", ["edits"] = edits },
                CancellationToken.None
            );
            await Assert.That(result).Contains("1 edit");
            await Assert.That(File.ReadAllText(file)).IsEqualTo("hi world");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_MultiEdit()
    {
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-edit-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        var file = Path.Combine(tmp, "f.txt");
        File.WriteAllText(file, "line1\nline2\nline3");
        try
        {
            var handler = EditTool.Create(tmp);
            var edits = new[]
            {
                new Dictionary<string, object?> { ["oldText"] = "line1", ["newText"] = "first" },
                new Dictionary<string, object?> { ["oldText"] = "line3", ["newText"] = "third" },
            };
            await handler(
                new Dictionary<string, object?> { ["path"] = "f.txt", ["edits"] = edits },
                CancellationToken.None
            );
            await Assert.That(File.ReadAllText(file)).IsEqualTo("first\nline2\nthird");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Create_DoesNotReplaceTextIntroducedByPreviousEdit()
    {
        // Bug: if edit1 introduces text matching edit2.oldText,
        // Replace() must not touch the newly introduced text.
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "pico-edit-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(tmp);
        var file = Path.Combine(tmp, "f.txt");
        File.WriteAllText(file, "ab");
        try
        {
            var handler = EditTool.Create(tmp);
            var edits = new[]
            {
                new Dictionary<string, object?> { ["oldText"] = "a", ["newText"] = "b" },
                new Dictionary<string, object?> { ["oldText"] = "b", ["newText"] = "y" },
            };
            await handler(
                new Dictionary<string, object?> { ["path"] = "f.txt", ["edits"] = edits },
                CancellationToken.None
            );
            // edit1: a → b  =>  "bb"
            // edit2: b → y  =>  should replace ONLY the original "b", not both
            await Assert.That(File.ReadAllText(file)).IsEqualTo("by");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
