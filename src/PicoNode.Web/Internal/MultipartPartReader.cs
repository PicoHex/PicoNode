using System.Text;

namespace PicoNode.Web.Internal;

internal sealed record MultipartPart(Dictionary<string, string> Headers, byte[]? Content);

internal static class MultipartPartReader
{
    public static async ValueTask<MultipartPart?> ReadPartAsync(
        MultipartBufferedReader reader,
        byte[] boundary,
        CancellationToken ct = default
    )
    {
        // Read headers until empty line (\r\n\r\n)
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                return null;
            if (line.Length == 0)
                break; // empty line = end of headers

            // Parse "Name: Value" header
            var colon = Array.IndexOf(line, (byte)':');
            if (colon > 0)
            {
                var name = Encoding.ASCII.GetString(line, 0, colon);
                var value = Encoding
                    .ASCII.GetString(line, colon + 1, line.Length - colon - 1)
                    .Trim();
                headers[name] = value;
            }
        }

        // Read content until next boundary
        var content = await reader.ReadUntilBoundaryAsync(boundary, ct);
        return new MultipartPart(headers, content);
    }
}
