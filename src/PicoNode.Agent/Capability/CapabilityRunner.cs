using System.Text.Json;
using PicoNode.AI;

namespace PicoNode.Agent;

public sealed class CapabilityRunner
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public static void ValidateArgs(ManifestCapability config, Dictionary<string, object> args)
    {
        if (config.Schema is null)
            return;
        using var doc = JsonDocument.Parse(config.Schema);
        var root = doc.RootElement;
        if (root.TryGetProperty("required", out var required))
        {
            foreach (var field in required.EnumerateArray())
            {
                var name = field.GetString()!;
                if (!args.ContainsKey(name))
                    throw new ToolException(
                        ToolErrorCode.SchemaValidationFailed,
                        $"Missing required parameter: {name}"
                    );
            }
        }
    }

    public readonly record struct TruncationResult(
        string Content,
        bool Truncated,
        string? FullOutputPath
    );

    public static TruncationResult TruncateOutput(string output, int maxBytes, int maxLines)
    {
        var lines = output.Split('\n');
        if (output.Length <= maxBytes && lines.Length <= maxLines)
            return new TruncationResult(output, false, null);

        var truncatedLines = lines.Take(maxLines).ToArray();
        var truncated = string.Join('\n', truncatedLines);
        if (truncated.Length > maxBytes)
            truncated = truncated[..maxBytes];

        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, output);
        var notice =
            $"[Output truncated: {truncated.Length} of {output.Length} bytes, {truncatedLines.Length} of {lines.Length} lines. Full output: {tempPath}]";
        return new TruncationResult(truncated + "\n" + notice, true, tempPath);
    }

    public async Task<JsonElement> ExecuteAsync(
        ManifestCapability config,
        string contextKind,
        byte[] inputJson,
        CancellationToken ct
    )
    {
        var startInfo = ParseHandler(config.Handler);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        var process =
            Process.Start(startInfo)
            ?? throw new ToolException(
                ToolErrorCode.ExecutionFailed,
                $"Failed to start capability handler: {config.Handler}"
            );

        // Kick off stderr drain concurrently so a chatty tool cannot deadlock
        // by filling the stderr pipe while we wait for stdout.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            // Write input line
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(inputJson, ct);
                await process.StandardInput.BaseStream.WriteAsync(NewLine, ct);
                await process.StandardInput.FlushAsync(ct);
                process.StandardInput.Close();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // stdin closed early by the subprocess — proceed to read whatever
                // it produced on stdout/stderr rather than masking the real error.
            }

            // Read response line
            var responseLine = await process.StandardOutput.ReadLineAsync(ct);

            // Wait for the subprocess to actually exit before inspecting exit code.
            await process.WaitForExitAsync(ct);

            var stderr = await stderrTask;

            if (string.IsNullOrEmpty(responseLine))
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"Capability '{config.Name}' exited with code {process.ExitCode} and no output"
                    : $"Capability '{config.Name}' produced no output (exit code {process.ExitCode}): {stderr.Trim()}";
                throw new ToolException(ToolErrorCode.ExecutionFailed, detail);
            }

            try
            {
                using var doc = JsonDocument.Parse(responseLine);
                return doc.RootElement.Clone();
            }
            catch (JsonException jex)
            {
                var stderrTail = string.IsNullOrWhiteSpace(stderr) ? "" : $" (stderr: {stderr.Trim()})";
                throw new ToolException(
                    ToolErrorCode.ExecutionFailed,
                    $"Capability '{config.Name}' returned malformed JSON: {jex.Message}{stderrTail}",
                    jex
                );
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            TryKill(process);
            process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — process may have already exited or be unkillable.
        }
    }

    private static ProcessStartInfo ParseHandler(string handler)
    {
        var parts = handler.Split(' ', 2);
        return parts.Length == 2
            ? new ProcessStartInfo(parts[0], parts[1])
            : new ProcessStartInfo(parts[0]);
    }
}
