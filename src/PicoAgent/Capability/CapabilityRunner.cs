namespace PicoAgent;

using System.Diagnostics;

public sealed class CapabilityRunner
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public async Task<JsonElement> ExecuteAsync(
        ManifestCapability config,
        string contextKind,
        byte[] inputJson,
        CancellationToken ct)
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
