namespace PicoNode.Agent;

public sealed class WriteTool : IBuiltInTool
{
    public string Name => "write";
    public string Description => "Create or overwrite a file with content. Creates parent directories automatically.";

    public string? InputSchema => """
    {
      "type": "object",
      "properties": {
        "path": { "type": "string", "description": "Path to the file to write" },
        "content": { "type": "string", "description": "Content to write to the file" }
      },
      "required": ["path", "content"]
    }
    """;

    public Task<(string Content, bool IsError)> ExecuteAsync(
        IReadOnlyDictionary<string, object?> args,
        string workingDirectory,
        CancellationToken ct)
    {
        var path = ReadTool.GetStringArg(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(("[Error: path is required]", true));

        var content = ReadTool.GetStringArg(args, "content");

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workingDirectory, path));

        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        File.WriteAllText(fullPath, content, Encoding.UTF8);
        return Task.FromResult(($"[Wrote {content.Length} bytes to {path}]", false));
    }
}
