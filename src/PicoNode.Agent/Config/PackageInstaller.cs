namespace PicoNode.Agent;

public static class PackageInstaller
{
    /// <summary>
    /// Ensure git repos exist for the given package entries. For paths where
    /// the directory does not already exist, attempt <c>git clone</c>.
    /// Failures are logged as warnings and skipped — they never crash startup.
    /// Non-git entries are silently ignored.
    /// </summary>
    public static async Task EnsureAsync(
        List<PackageEntry> entries,
        ILogger? logger,
        CancellationToken ct
    )
    {
        foreach (var entry in entries)
        {
            if (!entry.IsGit || entry.GitUrl is null)
                continue;
            if (Directory.Exists(entry.DisplayPath))
                continue;

            try
            {
                logger?.Log(
                    LogLevel.Info,
                    new EventId(0),
                    $"Cloning {entry.GitUrl} into {entry.DisplayPath}...",
                    null
                );
                Directory.CreateDirectory(Path.GetDirectoryName(entry.DisplayPath)!);
                await RunGitCloneAsync(entry.GitUrl, entry.GitRef, entry.DisplayPath, ct);
            }
            catch (Exception ex)
            {
                logger?.Log(
                    LogLevel.Warning,
                    new EventId(0),
                    $"Failed to clone {entry.GitUrl}: {ex.Message}. Package skipped.",
                    null
                );
            }
        }
    }

    private static async Task RunGitCloneAsync(
        string url,
        string? gitRef,
        string targetPath,
        CancellationToken ct
    )
    {
        var args = gitRef is { Length: > 0 }
            ? $"clone --branch \"{gitRef}\" --depth 1 \"{url}\" \"{targetPath}\""
            : $"clone --depth 1 \"{url}\" \"{targetPath}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"git clone failed (exit {process.ExitCode}): {stderr.Trim()}"
            );
        }
    }
}
