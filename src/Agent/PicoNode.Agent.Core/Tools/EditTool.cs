namespace PicoNode.Agent.Domain;

public static class EditTool
{
    public static string Schema =>
        """{"type":"object","properties":{"path":{"type":"string"},"edits":{"type":"array","items":{"type":"object","properties":{"oldText":{"type":"string"},"newText":{"type":"string"}},"required":["oldText","newText"]}}},"required":["path","edits"]}""";

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

            // All edits matched against original file content, not incrementally
            var original = await File.ReadAllTextAsync(fullPath, ct);
            var result = original;
            foreach (var (oldText, newText) in editPairs)
            {
                var count = CountOccurrences(original, oldText);
                if (count == 0)
                    return $"[Error: oldText not found in file: \"{oldText[..Math.Min(oldText.Length, 80)]}\"]";
                if (count > 1)
                    return $"[Error: oldText is not unique in file, found {count} occurrences]";
                result = result.Replace(oldText, newText);
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
