namespace PicoJsonRpc;

/// <summary>
/// NDJSON framing for the JSON-RPC 2.0 envelope stream (spec §9.1):
/// one JSON object per line, '\n' terminated; 16 MiB per-message cap; empty
/// lines skipped (EOF is expressed only by ReadAsync returning 0, never by a
/// blank line). Frames serialize via <see cref="JsonRpcWire"/> — hand-written
/// UTF-8 JSON, conforming to JSON-RPC 2.0 (T7.4 / D2 = A): every line carries
/// <c>"jsonrpc":"2.0"</c>, payloads are INLINE JSON values, a notification has
/// no <c>id</c>, and a response carries exactly one of <c>result</c>/<c>error</c>.
/// <para>
/// Malformed lines, over-limit frames, and — per the union discrimination rule —
/// frames carrying BOTH id and method (the child sent a request) throw
/// <see cref="FormatException"/> (fail-loud protocol discipline). A BATCH line
/// (an array) is the one inbound line that is answered instead of throwing:
/// batching is unsupported (README), so it yields an <c>Invalid Request</c>
/// (<c>-32600</c>) frame for the caller to send back and the stream keeps
/// flowing — a throw here would run into the client's <c>HandleEof</c> and kill
/// the channel for every later call.
/// </para>
/// </summary>
public static class NdjsonFramer
{
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static byte[] Encode<T>(T envelope)
        where T : class
    {
        // A frame has one wire shape, whichever face of the codec produced it.
        var jsonBytes =
            envelope is JsonRpcFrame frame ? JsonRpcWire.Encode(frame)
            : Encoding.UTF8.GetBytes(JsonRpcCodec.Serialize(envelope));
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
                    // snapshot the line and clear BEFORE yielding: the caller may
                    // never resume the enumerator, and the buffer must already be
                    // reusable for the next line.
                    var line = bytes.ToArray();
                    bytes.Clear();
                    if (IsBatchLine(line))
                    {
                        // batching unsupported — answered, not fatal
                        yield return BatchUnsupported();
                        continue;
                    }
                    var frame = JsonRpcWire.ReadFrame(line);
                    if (frame.Id is not null && frame.Method is not null)
                        throw new FormatException(
                            "child sent a request (response/notification only)"
                        );
                    yield return frame;
                    continue;
                }
                bytes.Add(b);
                if (bytes.Count > MaxFrameBytes)
                    throw new FormatException($"frame exceeds {MaxFrameBytes} bytes");
            }
        }
    }

    /// <summary>A batch (a top-level JSON array) — the only unsupported line
    /// shape. Leading whitespace (a stray CR from a CRLF writer) is skipped.</summary>
    private static bool IsBatchLine(byte[] line)
    {
        foreach (var b in line)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            return b == (byte)'[';
        }
        return false;
    }

    /// <summary>The answer to a batch line: a <c>-32600 Invalid Request</c> frame
    /// with a null id (JSON-RPC 2.0 §5 — an unanswerable request is answered with
    /// a null id), for the caller to write back.</summary>
    private static JsonRpcFrame BatchUnsupported() =>
        new()
        {
            Error = new JsonRpcError
            {
                Code = JsonRpcWire.InvalidRequestCode,
                Message = JsonRpcWire.BatchUnsupportedMessage,
            },
        };
}
