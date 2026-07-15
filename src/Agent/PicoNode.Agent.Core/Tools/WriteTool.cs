namespace PicoNode.Agent.Domain;

public static class WriteTool
{
    public static Tool Def =>
        new()
        {
            Name = "write",
            Description =
                "Create or overwrite a file with the given content. Creates parent directories as needed.",
            Kind = ToolKind.BuiltIn,
            InputSchema =
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""",
        };

    public static string Schema => Def.InputSchema!;

    public static Func<Dictionary<string, object?>, CancellationToken, Task<string>> Create(
        string cwd
    ) =>
        async (args, ct) =>
        {
            var path = Arg(args, "path");
            var content = Arg(args, "content");
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(fullPath, content, ct);
            return $"[Wrote {content.Length} bytes]";
        };

    private static string Arg(Dictionary<string, object?> args, string key) =>
        args.GetValueOrDefault(key)?.ToString() ?? "";
}
