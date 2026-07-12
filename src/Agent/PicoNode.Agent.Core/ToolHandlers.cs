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
        var hasBom = content.Length > 0 && content[0] == '\uFEFF';
        var text = hasBom ? content[1..] : content;
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");

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

        if (hasBom)
            restored = '\uFEFF' + restored;

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

        var rg = await ToolManager.EnsureToolAsync("rg", ct);
        if (rg is null)
            return $"[Error: ripgrep not available]";

        var command = $"\"{rg}\" -n --color=never --hidden -I \"{pattern}\" \"{dir}\"";
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

        var fd = await ToolManager.EnsureToolAsync("fd", ct);
        if (fd is null)
            return $"[Error: fd not available]";

        var command = $"\"{fd}\" --glob --color=never --hidden \"{name}\" \"{dir}\"";
        var result = await RunShellAsync(command, ct);
        return string.IsNullOrWhiteSpace(result) ? "No files found" : result;
    }

    private static string EscapeShellArg(string s) => s.Replace("\"", "\\\"");

    public static async Task<string> BashAsync(
        Dictionary<string, object?> args,
        CancellationToken ct
    )
    {
        var cmd = args.GetValueOrDefault("command")?.ToString() ?? "";
        return await RunShellAsync(cmd, ct);
    }

    public static (string Shell, string[] Args) GetShellConfig()
    {
        if (OperatingSystem.IsWindows())
        {
            var bashPaths = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Git",
                    "bin",
                    "bash.exe"
                ),
                @"C:\Program Files\Git\bin\bash.exe",
            };
            foreach (var p in bashPaths)
                if (File.Exists(p))
                    return (p, new[] { "-c" });
            return ("cmd.exe", new[] { "/c" });
        }
        return ("/bin/bash", new[] { "-c" });
    }

    private static async Task<string> RunShellAsync(string command, CancellationToken ct)
    {
        try
        {
            var (shell, args) = GetShellConfig();
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = shell,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var a in args)
                p.StartInfo.ArgumentList.Add(a);
            p.StartInfo.ArgumentList.Add(command);
            p.Start();
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return stdout.Trim() + (stderr.Length > 0 ? "\n" + stderr.Trim() : "");
        }
        catch (Exception ex)
        {
            ExceptionHandler.LogOnly(ex, "ToolHandlers.cs");
            return $"[Error: {ex.Message}]";
        }
    }
}
