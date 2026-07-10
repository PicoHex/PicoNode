namespace PicoNode.Agent;

public sealed class EditTool : IBuiltInTool
{
    public string Name => "edit";
    public string Description =>
        "Edit files via precise text replacement. Each edit replaces oldText with newText. oldText must be unique and edits must not overlap.";

    public string? InputSchema =>
        """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Path to the file to edit" },
                "edits": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "oldText": { "type": "string", "description": "Text to replace" },
                      "newText": { "type": "string", "description": "Replacement text" }
                    },
                    "required": ["oldText", "newText"]
                  }
                }
              },
              "required": ["path", "edits"]
            }
            """;

    public Task<(string Content, bool IsError)> ExecuteAsync(
        IReadOnlyDictionary<string, object?> args,
        string workingDirectory,
        CancellationToken ct
    )
    {
        var path = BuiltInToolHelpers.GetStringArg(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(("[Error: path is required]", true));

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workingDirectory, path));

        if (!File.Exists(fullPath))
            return Task.FromResult(($"[Error: File not found: {path}]", true));

        if (
            !args.TryGetValue("edits", out var editsObj)
            || editsObj is not List<object?> editList
            || editList.Count == 0
        )
            return Task.FromResult(("[Error: edits is required and must be non-empty]", true));

        var edits = new List<(string OldText, string NewText, int Index)>();
        for (int i = 0; i < editList.Count; i++)
        {
            if (editList[i] is not Dictionary<string, object?> edit)
                return Task.FromResult(
                    ($"[Error: edits[{i}] must be an object with oldText and newText]", true)
                );
            var oldText = BuiltInToolHelpers.GetStringArg(edit, "oldText");
            var newText = BuiltInToolHelpers.GetStringArg(edit, "newText");
            if (string.IsNullOrEmpty(oldText))
                return Task.FromResult(($"[Error: edits[{i}].oldText is required]", true));
            edits.Add((oldText, newText, i));
        }

        var content = File.ReadAllText(fullPath);

        // Validate uniqueness
        var positions = new List<(int Start, int End, int EditIndex)>();
        for (int i = 0; i < edits.Count; i++)
        {
            var (oldText, _, editIdx) = edits[i];
            var idx = content.IndexOf(oldText, StringComparison.Ordinal);
            if (idx < 0)
                return Task.FromResult(
                    ($"[Error: oldText not found: \"{Truncate(oldText, 60)}\"]", true)
                );
            if (content.IndexOf(oldText, idx + 1, StringComparison.Ordinal) >= 0)
                return Task.FromResult(
                    ($"[Error: oldText appears multiple times: \"{Truncate(oldText, 60)}\"]", true)
                );
            positions.Add((idx, idx + oldText.Length, editIdx));
        }

        // Check no overlap
        positions.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < positions.Count; i++)
        {
            if (positions[i].Start < positions[i - 1].End)
                return Task.FromResult(
                    (
                        "[Error: edits overlap. Each oldText must be in a non-overlapping region.]",
                        true
                    )
                );
        }

        // Apply edits (in reverse order to preserve indices)
        positions.Reverse();
        var sb = new StringBuilder(content);
        foreach (var (start, end, editIdx) in positions)
        {
            var newText = edits[editIdx].NewText;
            sb.Remove(start, end - start);
            sb.Insert(start, newText);
        }

        File.WriteAllText(fullPath, sb.ToString());
        return Task.FromResult(($"[Applied {edits.Count} edit(s) to {path}]", false));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
