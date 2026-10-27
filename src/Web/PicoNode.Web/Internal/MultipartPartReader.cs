namespace PicoNode.Web.Internal;

/// <summary>
/// One parsed part. <paramref name="HeaderBytes"/> is the wire size of the part's header
/// block (the sum of <c>line.Length + 2</c> over its header lines, i.e. without the empty
/// line that terminates it), which is what the total-size budget charges for headers.
/// </summary>
internal sealed record MultipartPart(
    Dictionary<string, string> Headers,
    byte[]? Content,
    int HeaderBytes
);

internal static class MultipartPartReader
{
    public static async ValueTask<MultipartPart?> ReadPartAsync(
        MultipartBufferedReader reader,
        byte[] boundary,
        int maxPartSizeBytes = MultipartFormDataParserOptions.DefaultMaxPartSizeBytes,
        int maxHeaderBytesPerPart = MultipartFormDataParserOptions.DefaultMaxHeaderBytesPerPart,
        CancellationToken ct = default
    )
    {
        // Read headers until empty line (\r\n\r\n), charging every line against the
        // per-part header budget as it arrives: the bytes are attacker-supplied and were
        // previously read into strings the size limits never saw.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = 0;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                return null;
            if (line.Length == 0)
                break; // empty line = end of headers

            headerBytes += line.Length + 2;
            if (headerBytes > maxHeaderBytesPerPart)
            {
                throw new InvalidDataException("Multipart part headers exceed maximum size");
            }

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
        var content = await reader.ReadUntilBoundaryAsync(boundary, maxPartSizeBytes, ct);
        return new MultipartPart(headers, content, headerBytes);
    }
}
