namespace PicoJsonRpc;

/// <summary>
/// NDJSON framing for the JSON-RPC 2.0 envelope stream (spec §9.1):
/// one JSON object per line, '\n' terminated; 16 MiB per-message cap; empty
/// lines skipped (EOF is expressed only by ReadAsync returning 0, never by a
/// blank line). Frames serialize via <see cref="JsonRpcCodec"/> (camelCase
/// wire, PicoJetson). Malformed lines, over-limit frames, and — per the union
/// discrimination rule — frames carrying BOTH id and method (the child sent a
/// request) throw <see cref="FormatException"/> (fail-loud protocol discipline).
/// </summary>
public static class NdjsonFramer
{
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static byte[] Encode<T>(T envelope)
        where T : class
    {
        // A0 probe: Serialize<T> returns string; UTF8 bytes for the wire.
        var jsonBytes = Encoding.UTF8.GetBytes(JsonRpcCodec.Serialize(envelope));
        if (jsonBytes.Length > MaxFrameBytes)
            throw new InvalidOperationException($"frame exceeds {MaxFrameBytes} bytes");
        var withNl = new byte[jsonBytes.Length + 1];
        jsonBytes.CopyTo(withNl, 0);
        withNl[^1] = (byte)'\n';
        return withNl;
    }

    public static async IAsyncEnumerable<JsonRpcFrame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var bytes = new List<byte>(4096);
        var chunk = new byte[8192];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await stream.ReadAsync(chunk.AsMemory(), ct);
            if (n == 0)
            {
                if (bytes.Count > 0)
                    throw new FormatException("unterminated frame at EOF");
                yield break;
            }
            for (var i = 0; i < n; i++)
            {
                var b = chunk[i];
                if (b == (byte)'\n')
                {
                    if (bytes.Count == 0)
                        continue; // empty line — not EOF
                    if (bytes.Count > MaxFrameBytes)
                        throw new FormatException($"frame exceeds {MaxFrameBytes} bytes");
                    var frame =
                        JsonSerializer.Deserialize<JsonRpcFrame>(
                            bytes.ToArray(),
                            JsonRpcCodec.Camel
                        ) ?? throw new FormatException("null JSON frame");
                    if (frame.Id is not null && frame.Method is not null)
                        throw new FormatException(
                            "child sent a request (response/notification only)"
                        );
                    yield return frame;
                    bytes.Clear();
                    continue;
                }
                bytes.Add(b);
                if (bytes.Count > MaxFrameBytes)
                    throw new FormatException($"frame exceeds {MaxFrameBytes} bytes");
            }
        }
    }
}
