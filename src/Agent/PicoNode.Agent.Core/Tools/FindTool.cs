namespace PicoNode.Agent.Domain;

public static class FindTool
{
    private const int DefaultLimit = 1000;

    public static Tool Def =>
        new()
        {
            Name = "find",
            Description =
                "Find files matching a glob pattern. Supports recursive directory search.",
            Kind = ToolKind.BuiltIn,
            InputSchema =
                """{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"limit":{"type":"integer"}},"required":["pattern"]}""",
        };

    public static string Schema => Def.InputSchema!;

    public static Func<Dictionary<string, object?>, CancellationToken, Task<string>> Create(
        string cwd
    ) =>
        async (args, ct) =>
        {
            var pattern = Arg(args, "pattern");
            var path = Arg(args, "path", cwd);
            var limit = ArgInt(args, "limit", DefaultLimit);
            var searchDir = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
            if (!Directory.Exists(searchDir))
                return $"[Error: Directory not found: {searchDir}]";
            var files = Directory
                .GetFiles(searchDir, pattern, SearchOption.AllDirectories)
                .Take(limit)
                .ToArray();
            return files.Length == 0 ? "[No files found]" : string.Join("\n", files);
        };

    private static string Arg(Dictionary<string, object?> args, string key, string def = "") =>
        args.GetValueOrDefault(key)?.ToString() ?? def;

    private static int ArgInt(Dictionary<string, object?> args, string key, int def) =>
        int.TryParse(args.GetValueOrDefault(key)?.ToString(), out var v) ? v : def;
}
