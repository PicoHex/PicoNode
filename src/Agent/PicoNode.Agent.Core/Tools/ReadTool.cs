using System.Text;

namespace PicoNode.Agent.Domain;

public static class ReadTool
{
    private const int MaxLines = 2000;
    private const int MaxBytes = 50 * 1024;

    public static Tool Def =>
        new()
        {
            Name = "read",
            Description =
                "Read file contents. Supports text files and images (jpg, png, gif, webp, bmp). "
                + "For text files, output is truncated to 2000 lines or 50KB. "
                + "Use offset/limit for large files.",
            Kind = ToolKind.BuiltIn,
            InputSchema =
                """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer"}},"required":["path"]}""",
        };

    public static string Schema => Def.InputSchema!;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp",
        ".bmp",
    };

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
                // Truncate by bytes, not chars, to avoid splitting multi-byte
                // UTF-8 characters or surrogate pairs.
                var rawBytes = Encoding.UTF8.GetBytes(output);
                // Decode the first MaxBytes bytes back to a string.
                // GetString handles incomplete sequences by dropping the
                // trailing incomplete multi-byte character.
                output = Encoding.UTF8.GetString(rawBytes, 0, MaxBytes);
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
