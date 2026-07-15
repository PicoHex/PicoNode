namespace PicoNode.Agent.Domain;

public static class LsTool
{
    private const int DefaultLimit = 500;

    public static string Schema =>
        """{"type":"object","properties":{"path":{"type":"string"},"limit":{"type":"integer"}}}""";

    public static Func<Dictionary<string, object?>, CancellationToken, Task<string>> Create(
        string cwd
    ) =>
        async (args, ct) =>
        {
            var path = Arg(args, "path", cwd);
            var limit = ArgInt(args, "limit", DefaultLimit);
            var dir = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
            if (!Directory.Exists(dir))
                return $"[Error: Directory not found: {dir}]";
            var entries = Directory.GetFileSystemEntries(dir).Take(limit).ToArray();
            var result = string.Join("\n", entries);
            if (entries.Length >= limit)
                result += $"\n... (truncated at {limit} entries)";
            return result;
        };

    private static string Arg(Dictionary<string, object?> args, string key, string def = "") =>
        args.GetValueOrDefault(key)?.ToString() ?? def;

    private static int ArgInt(Dictionary<string, object?> args, string key, int def) =>
        int.TryParse(args.GetValueOrDefault(key)?.ToString(), out var v) ? v : def;
}
