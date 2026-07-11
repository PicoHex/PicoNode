namespace PicoNode.Agent.Domain;

/// <summary>
/// Handler implementations for built-in tools. Public static so tests can call them directly.
/// </summary>
public static class ToolHandlers
{
    public static async Task<string> EditAsync(
        Dictionary<string, object?> args,
        CancellationToken ct
    )
    {
        var p = args.GetValueOrDefault("path")?.ToString() ?? "";
        var oldText = args.GetValueOrDefault("oldText")?.ToString() ?? "";
        var newText = args.GetValueOrDefault("newText")?.ToString() ?? "";

        if (!File.Exists(p))
            return $"[Error: File not found: {p}]";

        var content = await File.ReadAllTextAsync(p, ct);
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");

        var matches = Regex.Matches(normalized, Regex.Escape(oldText));
        if (matches.Count == 0)
            return $"[Error: oldText not found in {Path.GetFileName(p)}]";
        if (matches.Count > 1)
            return $"[Error: oldText matches {matches.Count} locations, must be unique]";

        var result = normalized.Replace(oldText, newText, StringComparison.Ordinal);
        var restored =
            content.Contains("\r\n") ? result.Replace("\n", "\r\n")
            : content.Contains("\r") ? result.Replace("\n", "\r")
            : result;

        await File.WriteAllTextAsync(p, restored, ct);
        return $"[Replaced 1 block in {Path.GetFileName(p)}]";
    }

    public static async Task<string> GrepAsync(
        Dictionary<string, object?> args,
        CancellationToken ct
    )
    {
        var pattern = EscapeShellArg(args.GetValueOrDefault("pattern")?.ToString() ?? "");
        var dir = EscapeShellArg(args.GetValueOrDefault("path")?.ToString() ?? ".");

        if (!Directory.Exists(dir))
            return $"[Error: Directory not found: {dir}]";

        var command = OperatingSystem.IsWindows()
            ? $"findstr /s /n /i \"{pattern}\" \"{dir}\\*\""
            : $"grep -rn -I \"{pattern}\" \"{dir}\"";

        var result = await RunShellAsync(command, ct);
        return string.IsNullOrWhiteSpace(result) ? "No matches found" : result;
    }

    public static async Task<string> FindAsync(
        Dictionary<string, object?> args,
        CancellationToken ct
    )
    {
        var name = EscapeShellArg(args.GetValueOrDefault("name")?.ToString() ?? "*");
        var dir = EscapeShellArg(args.GetValueOrDefault("path")?.ToString() ?? ".");

        if (!Directory.Exists(dir))
            return $"[Error: Directory not found: {dir}]";

        var command = OperatingSystem.IsWindows()
            ? $"dir /s /b \"{Path.Combine(dir, name)}\" 2>nul"
            : $"find \"{dir}\" -name \"{name}\"";

        var result = await RunShellAsync(command, ct);
        return string.IsNullOrWhiteSpace(result) ? "No files found" : result;
    }

    private static string EscapeShellArg(string s) => s.Replace("\"", "\\\"");

    private static async Task<string> RunShellAsync(string command, CancellationToken ct)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                    Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            p.Start();
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return stdout.Trim() + (stderr.Length > 0 ? "\n" + stderr.Trim() : "");
        }
        catch (Exception ex)
        {
            return $"[Error: {ex.Message}]";
        }
    }
}
