namespace PicoNode.Agent;

public sealed class BashTool : IBuiltInTool
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxLines = 2000;
    private const int MaxBytes = 50000;

    public string Name => "bash";
    public string Description =>
        "Execute a shell command. Returns stdout+stderr. Set timeout to limit execution time.";

    public string? InputSchema =>
        """
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "Shell command to execute" },
                "timeout": { "type": "integer", "description": "Timeout in seconds (default 30)" },
                "workdir": { "type": "string", "description": "Working directory for the command" }
              },
              "required": ["command"]
            }
            """;

    public async Task<(string Content, bool IsError)> ExecuteAsync(
        IReadOnlyDictionary<string, object?> args,
        string workingDirectory,
        CancellationToken ct
    )
    {
        var command = BuiltInToolHelpers.GetStringArg(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ("[Error: command is required]", true);

        var timeoutSecs = (int)
            BuiltInToolHelpers.GetLongArg(args, "timeout", DefaultTimeoutSeconds);
        var workdir = BuiltInToolHelpers.GetStringArg(args, "workdir");
        if (string.IsNullOrWhiteSpace(workdir))
            workdir = workingDirectory;

        var (shell, shellFlag) = GetShell();
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = $"{shellFlag} {command}",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSecs));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            process.Start();

            // ReadToEndAsync without token so pipes drain even after process is killed
            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();

            // WaitForExitAsync throws OperationCanceledException on timeout or caller cancel
            await process.WaitForExitAsync(linkedCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
                output += (output.Length > 0 ? "\n" : "") + stderr;

            if (string.IsNullOrWhiteSpace(output))
                output = $"[Command exited with code {process.ExitCode}]";

            var truncated = CapabilityRunner.TruncateOutput(output, MaxBytes, MaxLines);
            return (truncated.Content, process.ExitCode != 0);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            // Drain any buffered output that was written before the process was killed
            string stdout = "",
                stderr = "";
            try
            {
                if (stdoutTask is { IsCompletedSuccessfully: true })
                    stdout = stdoutTask.Result;
                if (stderrTask is { IsCompletedSuccessfully: true })
                    stderr = stderrTask.Result;
            }
            catch
            { /* best-effort: pipes may already be closed */
            }

            var prefix = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
                prefix += (prefix.Length > 0 ? "\n" : "") + stderr;

            var timeoutMsg = timeoutCts.IsCancellationRequested
                ? $"\n[Command timed out after {timeoutSecs}s]"
                : "\n[Command cancelled]";

            var output = prefix + timeoutMsg;
            var truncated = CapabilityRunner.TruncateOutput(output, MaxBytes, MaxLines);
            return (truncated.Content, true);
        }
    }

    private static (string shell, string flag) GetShell()
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", "/c");
        return ("/bin/bash", "-c");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        { /* best-effort */
        }
    }
}
