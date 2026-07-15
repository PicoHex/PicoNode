using System.Text;

namespace PicoNode.Agent.Domain;

public static class BashTool
{
    private const int MaxLines = 2000;
    private const int MaxBytes = 50 * 1024;

    public static string Schema =>
        """{"type":"object","properties":{"command":{"type":"string"},"timeout":{"type":"integer"}},"required":["command"]}""";

    public static Func<Dictionary<string, object?>, CancellationToken, Task<string>> Create(
        string cwd
    ) =>
        async (args, ct) =>
        {
            var command = Arg(args, "command");
            var timeoutSecs = ArgInt(args, "timeout", 0);

            var (shell, shellArgs) = GetShellConfig();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = shell,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = cwd,
                },
            };
            foreach (var sa in shellArgs)
                process.StartInfo.ArgumentList.Add(sa);
            process.StartInfo.ArgumentList.Add(command);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var finished = timeoutSecs > 0 ? process.WaitForExit(timeoutSecs * 1000) : true;
            if (!finished)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch { }
                process.WaitForExit(1000);
                return $"[Timeout after {timeoutSecs}s]";
            }

            process.WaitForExit();
            var output = stdout.ToString() + (stderr.Length > 0 ? "\n" + stderr.ToString() : "");
            var lines = output.Split('\n');
            var byteCount = Encoding.UTF8.GetByteCount(output);
            if (lines.Length > MaxLines || byteCount > MaxBytes)
            {
                var truncated = string.Join("\n", lines.Take(MaxLines));
                var tempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempFile, output, ct);
                return truncated
                    + $"\n\n[Output truncated. Full output ({lines.Length} lines) saved to: {tempFile}]";
            }
            return output.TrimEnd();
        };

    internal static (string Shell, string[] Args) GetShellConfig()
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

    private static string Arg(Dictionary<string, object?> args, string key, string def = "") =>
        args.GetValueOrDefault(key)?.ToString() ?? def;

    private static int ArgInt(Dictionary<string, object?> args, string key, int def) =>
        int.TryParse(args.GetValueOrDefault(key)?.ToString(), out var v) ? v : def;
}
