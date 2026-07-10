namespace PicoNode.Agent;

public sealed class GrepTool : IBuiltInTool
{
    private const int DefaultLimit = 100;
    private const int MaxBytes = 50000;
    private const int MaxFiles = 5000;

    public string Name => "grep";
    public string Description =>
        "Search file contents for a pattern. Returns matching lines with file path and line number.";

    public string? InputSchema =>
        """
            {
              "type": "object",
              "properties": {
                "pattern": { "type": "string", "description": "Pattern to search for (regex)" },
                "path": { "type": "string", "description": "Directory to search (default: cwd)" },
                "include": { "type": "string", "description": "File glob filter (e.g. *.cs)" },
                "ignoreCase": { "type": "boolean", "description": "Case-insensitive search" },
                "limit": { "type": "integer", "description": "Maximum matches (default 100)" }
              },
              "required": ["pattern"]
            }
            """;

    public Task<(string Content, bool IsError)> ExecuteAsync(
        IReadOnlyDictionary<string, object?> args,
        string workingDirectory,
        CancellationToken ct
    )
    {
        var pattern = BuiltInToolHelpers.GetStringArg(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(("[Error: pattern is required]", true));

        var path = BuiltInToolHelpers.GetStringArg(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            path = workingDirectory;

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workingDirectory, path));

        if (!Directory.Exists(fullPath))
            return Task.FromResult(($"[Error: Directory not found: {path}]", true));

        var include = BuiltInToolHelpers.GetStringArg(args, "include");
        var ignoreCase = args.TryGetValue("ignoreCase", out var ic) && ic is bool b && b;
        var limit = (int)BuiltInToolHelpers.GetLongArg(args, "limit", DefaultLimit);

        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        Regex regex;
        try
        {
            regex = new Regex(pattern, options | RegexOptions.Compiled);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(($"[Error: Invalid regex pattern: {pattern}]", true));
        }

        var results = new List<string>();
        var filesScanned = 0;

        try
        {
            var searchPattern = string.IsNullOrWhiteSpace(include) ? "*" : include;
            var files = Directory.EnumerateFiles(
                fullPath,
                searchPattern,
                SearchOption.AllDirectories
            );

            foreach (var file in files)
            {
                if (filesScanned++ >= MaxFiles || results.Count >= limit)
                    break;

                try
                {
                    var lines = File.ReadLines(file);
                    var lineNum = 0;
                    foreach (var line in lines)
                    {
                        if (results.Count >= limit)
                            break;
                        lineNum++;
                        if (regex.IsMatch(line))
                        {
                            var relPath = Path.GetRelativePath(fullPath, file);
                            results.Add($"{relPath}:{lineNum}: {line}");
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
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
