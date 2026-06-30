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

        using var process = Process.Start(startInfo)!;

        // Write input line
        await process.StandardInput.BaseStream.WriteAsync(inputJson, ct);
        await process.StandardInput.BaseStream.WriteAsync(NewLine, ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        // Read response — single line, parse as JSON
        // v1: single response. v2: streaming with health ping/pong for persistent hooks.
        var responseLine = await process.StandardOutput.ReadLineAsync(ct);
        if (responseLine == null)
            return default;

        using var doc = JsonDocument.Parse(responseLine);
        return doc.RootElement.Clone();
    }

    private static ProcessStartInfo ParseHandler(string handler)
    {
        var parts = handler.Split(' ', 2);
        return parts.Length == 2
            ? new ProcessStartInfo(parts[0], parts[1])
            : new ProcessStartInfo(parts[0]);
    }
}
