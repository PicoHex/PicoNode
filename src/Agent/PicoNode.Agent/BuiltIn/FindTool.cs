namespace PicoNode.Agent;

public sealed class FindTool : IBuiltInTool
{
    private const int DefaultLimit = 1000;
    private const int MaxBytes = 50000;

    public string Name => "find";
    public string Description => "Find files by glob pattern. Returns matching file paths relative to search directory.";

    public string? InputSchema => """
    {
      "type": "object",
      "properties": {
        "pattern": { "type": "string", "description": "Glob pattern (e.g. *.cs). Supports *, ?, [...]" },
        "path": { "type": "string", "description": "Directory to search (default: cwd)" },
        "limit": { "type": "integer", "description": "Maximum results (default 1000)" }
      },
      "required": ["pattern"]
    }
    """;

    public Task<(string Content, bool IsError)> ExecuteAsync(
        IReadOnlyDictionary<string, object?> args,
        string workingDirectory,
        CancellationToken ct)
    {
        var pattern = BuiltInToolHelpers.GetStringArg(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(("[Error: pattern is required]", true));

        var path = BuiltInToolHelpers.GetStringArg(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            path = workingDirectory;

        var fullPath = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(workingDirectory, path));

        if (!Directory.Exists(fullPath))
            return Task.FromResult(($"[Error: Directory not found: {path}]", true));

        var limit = (int)BuiltInToolHelpers.GetLongArg(args, "limit", DefaultLimit);

        var results = new List<string>();
        try
        {
            var files = Directory.EnumerateFiles(fullPath, pattern, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (results.Count >= limit) break;
                results.Add(Path.GetRelativePath(fullPath, file));
            }
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(($"[Error: Directory not found: {path}]", true));
        }

        var output = string.Join('\n', results);
        var truncated = CapabilityRunner.TruncateOutput(output, MaxBytes, DefaultLimit);
        return Task.FromResult((truncated.Content, false));
    }
}
