namespace PicoNode.Agent;

public sealed class ReadTool : IBuiltInTool
{
    private const int MaxLines = 2000;
    private const int MaxBytes = 50000;
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB warning threshold

    public string Name => "read";
    public string Description => "Read file contents. Supports text files. Use offset/limit for large files.";

    public string? InputSchema => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Path to the file to read" },
        "offset": { "type": "integer", "description": "Line number to start reading from (1-indexed)" },
        "limit": { "type": "integer", "description": "Maximum number of lines to read" }
      },
      "required": ["path"]
    }
    """;

    public Task<(string Content, bool IsError)> ExecuteAsync(
        IReadOnlyDictionary<string, object?> args,
        string workingDirectory,
        CancellationToken ct)
    {
        var path = BuiltInToolHelpers.GetStringArg(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(("[Error: path is required]", true));

        var fullPath = ResolvePath(path, workingDirectory);

        if (!File.Exists(fullPath))
            return Task.FromResult(($"[Error: File not found: {path}]", true));

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxFileSize)
            return Task.FromResult(($"[File too large: {fileInfo.Length} bytes. Use offset/limit to read segments.]", true));

        // Binary detection: check first 8KB for null bytes
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[Math.Min(8192, fileInfo.Length)];
        var bytesRead = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var checkSpan = header.AsSpan(0, bytesRead);
        if (checkSpan.Contains((byte)0))
            return Task.FromResult(($"[Binary file: {fileInfo.Length} bytes]", true));

        fs.Seek(0, SeekOrigin.Begin);

        var offset = (int)BuiltInToolHelpers.GetLongArg(args, "offset", 1);
        var limit = (int)BuiltInToolHelpers.GetLongArg(args, "limit", MaxLines);

        using var reader = new StreamReader(fs, Encoding.UTF8);
        var lines = new List<string>();
        var lineCount = 0;
        while (reader.ReadLine() is { } line)
        {
            lineCount++;
            if (lineCount < offset)
                continue;
            if (lines.Count >= limit)
                break;
            lines.Add(line);
        }

        var output = string.Join('\n', lines);
        var truncated = CapabilityRunner.TruncateOutput(output, MaxBytes, MaxLines);
        return Task.FromResult((truncated.Content, false));
    }

    private static string ResolvePath(string path, string workingDirectory)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(workingDirectory, path));
    }
}
