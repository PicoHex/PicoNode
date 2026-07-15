using System.Text;

namespace PicoNode.Agent.Domain;

public static class ReadTool
{
    private const int MaxLines = 2000;
    private const int MaxBytes = 50 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp",
        ".bmp",
    };

    public static string Schema =>
        """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer"}},"required":["path"]}""";

    public static Func<Dictionary<string, object?>, CancellationToken, Task<string>> Create(
        string cwd
    ) =>
        async (args, ct) =>
        {
            var path = Arg(args, "path");
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
            if (!File.Exists(fullPath))
                return $"[Error: File not found: {fullPath}]";

            var ext = Path.GetExtension(fullPath);
            if (ImageExtensions.Contains(ext))
                return $"[Image file: {ext}]";

            var allLines = await File.ReadAllLinesAsync(fullPath, ct);
            var offset = ArgInt(args, "offset", 1);
            var startLine = Math.Max(0, offset - 1);
            if (startLine >= allLines.Length)
                return $"[Offset {offset} beyond end of file ({allLines.Length} lines)]";

            var limit = args.ContainsKey("limit") ? ArgInt(args, "limit", MaxLines) : -1;
            var endLine =
                limit > 0 ? Math.Min(startLine + limit, allLines.Length) : allLines.Length;
            var selected = allLines[startLine..endLine];
            var output = string.Join("\n", selected);
            var bytes = Encoding.UTF8.GetByteCount(output);
            if (bytes > MaxBytes)
            {
                output = output[..Math.Min(output.Length, MaxBytes)];
                return output + $"\n\n[Truncated to {MaxBytes / 1024}KB. Use offset to continue.]";
            }
            if (endLine < allLines.Length)
                output +=
                    $"\n\n[{allLines.Length - endLine} more lines. Use offset={endLine + 1} to continue.]";
            return output;
        };

    private static string Arg(Dictionary<string, object?> args, string key, string def = "") =>
        args.GetValueOrDefault(key)?.ToString() ?? def;

    private static int ArgInt(Dictionary<string, object?> args, string key, int def) =>
        int.TryParse(args.GetValueOrDefault(key)?.ToString(), out var v) ? v : def;
}
