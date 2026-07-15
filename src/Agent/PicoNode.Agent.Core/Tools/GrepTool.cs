using System.Text;
using System.Text.RegularExpressions;

namespace PicoNode.Agent.Domain;

public static class GrepTool
{
    private const int DefaultLimit = 100;
    private const int MaxLineChars = 500;
    private const int MaxBytes = 50 * 1024;

    public static Tool Def =>
        new()
        {
            Name = "grep",
            Description =
                "Search file contents for a pattern. "
                + "Returns matching lines with file paths and line numbers. "
                + "Supports glob filtering, case-insensitive search, literal mode, and context lines.",
            Kind = ToolKind.BuiltIn,
            InputSchema =
                """{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"glob":{"type":"string"},"ignoreCase":{"type":"boolean"},"literal":{"type":"boolean"},"context":{"type":"integer"},"limit":{"type":"integer"}},"required":["pattern"]}""",
        };

    public static string Schema => Def.InputSchema!;

    public static Func<Dictionary<string, object?>, CancellationToken, Task<string>> Create(
        string cwd
    ) =>
        async (args, ct) =>
        {
            var pattern = Arg(args, "pattern");
            var path = Arg(args, "path", cwd);
            var glob = Arg(args, "glob", "");
            var ignoreCase = ArgBool(args, "ignoreCase");
            var literal = ArgBool(args, "literal");
            var context = ArgInt(args, "context", 0);
            var limit = ArgInt(args, "limit", DefaultLimit);

            var searchDir = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
            if (!Directory.Exists(searchDir) && !File.Exists(searchDir))
                return $"[Error: Path not found: {searchDir}]";

            var files = File.Exists(searchDir)
                ? new[] { searchDir }
                : Directory.GetFiles(
                    searchDir,
                    string.IsNullOrEmpty(glob) ? "*" : glob,
                    SearchOption.AllDirectories
                );

            var regexOptions = RegexOptions.None;
            if (ignoreCase)
                regexOptions |= RegexOptions.IgnoreCase;
            var regex = literal
                ? new Regex(Regex.Escape(pattern), regexOptions)
                : new Regex(pattern, regexOptions);

            var results = new List<string>();
            var totalBytes = 0;
            foreach (var file in files)
            {
                if (results.Count >= limit)
                    break;
                var lines = await File.ReadAllLinesAsync(file, ct);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (results.Count >= limit)
                        break;
                    if (!regex.IsMatch(lines[i]))
                        continue;

                    var firstLine = Math.Max(0, i - context);
                    var lastLine = Math.Min(lines.Length - 1, i + context);
                    for (int j = firstLine; j <= lastLine; j++)
                    {
                        var prefix = j == i ? ">" : " ";
                        var line = lines[j];
                        if (line.Length > MaxLineChars)
                            line = line[..MaxLineChars] + "...";
                        results.Add($"{file}:{j + 1}:{prefix} {line}");
                        totalBytes += Encoding.UTF8.GetByteCount(results.Last());
                    }
                    if (totalBytes > MaxBytes)
                        break;
                }
            }
            if (totalBytes > MaxBytes)
                results.Add("[Truncated to 50KB]");
            return results.Count == 0 ? "[No matches]" : string.Join("\n", results);
        };

    private static string Arg(Dictionary<string, object?> args, string key, string def = "") =>
        args.GetValueOrDefault(key)?.ToString() ?? def;

    private static int ArgInt(Dictionary<string, object?> args, string key, int def) =>
        int.TryParse(args.GetValueOrDefault(key)?.ToString(), out var v) ? v : def;

    private static bool ArgBool(Dictionary<string, object?> args, string key) =>
        args.GetValueOrDefault(key)?.ToString()?.ToLowerInvariant() == "true";
}
