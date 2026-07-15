namespace PicoNode.Agent.Domain;

public static class EditTool
{
    public static Tool Def =>
        new()
        {
            Name = "edit",
            Description =
                "Make precise file edits with exact text replacement. "
                + "Supports multiple disjoint edits in one call via the edits array.",
            Kind = ToolKind.BuiltIn,
            InputSchema =
                """{"type":"object","properties":{"path":{"type":"string"},"edits":{"type":"array","items":{"type":"object","properties":{"oldText":{"type":"string"},"newText":{"type":"string"}},"required":["oldText","newText"]}}},"required":["path","edits"]}""",
        };

    public static string Schema => Def.InputSchema!;

    public static Func<Dictionary<string, object?>, CancellationToken, Task<string>> Create(
        string cwd
    ) =>
        async (args, ct) =>
        {
            var path = Arg(args, "path");
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
            if (!File.Exists(fullPath))
                return $"[Error: File not found: {fullPath}]";

            var editsRaw = args.GetValueOrDefault("edits");
            if (editsRaw is not IEnumerable<object> editList)
                return "[Error: edits must be an array]";

            var editPairs = editList
                .OfType<Dictionary<string, object?>>()
                .Select(e => (oldText: Arg(e, "oldText"), newText: Arg(e, "newText")))
                .Where(e => e.oldText.Length > 0)
                .ToArray();

            if (editPairs.Length == 0)
                return "[Error: no valid edits]";

            // Each edit is located in the original text and applied to the result
            // at the corresponding position (adjusted by cumulative offset from
            // earlier edits). This ensures text introduced by edit N is never
            // matched by edit N+1.
            var original = await File.ReadAllTextAsync(fullPath, ct);
            var result = original;
            var cumulativeOffset = 0;
            foreach (var (oldText, newText) in editPairs)
            {
                var count = CountOccurrences(original, oldText);
                if (count == 0)
                    return $"[Error: oldText not found in file: \"{oldText[..Math.Min(oldText.Length, 80)]}\"]";
                if (count > 1)
                    return $"[Error: oldText is not unique in file, found {count} occurrences]";
                var idx = original.IndexOf(oldText, StringComparison.Ordinal);
                var applyAt = idx + cumulativeOffset;
                result = result[..applyAt] + newText + result[(applyAt + oldText.Length)..];
                cumulativeOffset += newText.Length - oldText.Length;
            }
            await File.WriteAllTextAsync(fullPath, result, ct);
            return $"[Applied {editPairs.Length} edit(s)]";
        };

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0,
            pos = 0;
        while ((pos = text.IndexOf(pattern, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += pattern.Length;
        }
        return count;
    }

    private static string Arg(Dictionary<string, object?> args, string key) =>
        args.GetValueOrDefault(key)?.ToString() ?? "";
}
