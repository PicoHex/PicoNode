namespace PicoNode.Agent;

public sealed class LsTool : IBuiltInTool
{
    private const int DefaultLimit = 500;
    private const int MaxBytes = 50000;

    public string Name => "ls";
    public string Description => "List directory contents. Sorted alphabetically, directories suffixed with /.";

    public string? InputSchema => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Directory to list (default: current working directory)" },
        "limit": { "type": "integer", "description": "Maximum entries (default 500)" }
      }
    }
    """;

    public Task<(string Content, bool IsError)> ExecuteAsync(
        IReadOnlyDictionary<string, object?> args,
        string workingDirectory,
        CancellationToken ct)
    {
        var path = BuiltInToolHelpers.GetStringArg(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            path = workingDirectory;

        var fullPath = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(workingDirectory, path));

        if (!Directory.Exists(fullPath))
            return Task.FromResult(($"[Error: Directory not found: {path}]", true));

        var limit = (int)BuiltInToolHelpers.GetLongArg(args, "limit", DefaultLimit);

        var entries = new List<string>();
        foreach (var entry in Directory.GetFileSystemEntries(fullPath))
        {
            if (entries.Count >= limit) break;
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry)) name += "/";
            entries.Add(name);
        }

        entries.Sort(StringComparer.Ordinal);

        var output = string.Join('\n', entries);
        var truncated = CapabilityRunner.TruncateOutput(output, MaxBytes, DefaultLimit);
        return Task.FromResult((truncated.Content, false));
    }
}
