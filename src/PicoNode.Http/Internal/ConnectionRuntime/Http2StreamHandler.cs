namespace PicoNode.Http.Internal.ConnectionRuntime;

using PicoNode.Http.Internal.Hpack;

internal static class Http2StreamHandler
{
    private const int SingleStreamId = 1;

    /// <summary>
    /// Process a single HTTP/2 HEADERS frame through the request pipeline.
    /// Returns true if the connection should be closed.
    /// </summary>
    public static async ValueTask<bool> ProcessHeadersFrame(
        ITcpConnectionContext connection,
        Http2Frame frame,
        HttpRequestHandler requestHandler,
        CancellationToken ct
    )
    {
        // Validate stream — MVP supports only stream 1
        if (frame.StreamId != SingleStreamId)
        {
            await SendGoAwayAndCloseAsync(
                connection,
                Http2ErrorCode.ProtocolError,
                ct
            );
            return true;
        }

        // Require END_HEADERS — CONTINUATION not supported in MVP
        if (!frame.HasFlag(Http2FrameFlags.EndHeaders))
        {
            await SendGoAwayAndCloseAsync(
                connection,
                Http2ErrorCode.ProtocolError,
                ct
            );
            return true;
        }

        // Decode HPACK header block
        if (!HpackDecoder.TryDecode(frame.Payload.Span, out var headerFields))
        {
            await SendGoAwayAndCloseAsync(
                connection,
                Http2ErrorCode.CompressionError,
                ct
            );
            return true;
        }

        // Extract pseudo-headers and regular headers
        string? method = null;
        string? path = null;
        string? scheme = null;
        string? authority = null;
        var regularHeaders = new List<KeyValuePair<string, string>>();
        var headerDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headerFields)
        {
            switch (name)
            {
                case ":method":
                    method = value;
                    break;
                case ":path":
                    path = value;
                    break;
                case ":scheme":
                    scheme = value;
                    break;
                case ":authority":
                    authority = value;
                    break;
                default:
                    regularHeaders.Add(new(name, value));
                    if (!headerDict.ContainsKey(name))
                        headerDict[name] = value;
                    break;
            }
        }

        // Validate required pseudo-headers
        if (method is null || path is null)
        {
            await SendGoAwayAndCloseAsync(
                connection,
                Http2ErrorCode.ProtocolError,
                ct
            );
            return true;
        }

        // Construct HttpRequest
        var request = new HttpRequest
        {
            Method = method,
            Target = path,
            Version = "HTTP/2",
            HeaderFields = regularHeaders,
            Headers = headerDict,
        };

        // Invoke request handler
        HttpResponse response;
        try
        {
            response = await requestHandler(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            response = new HttpResponse
            {
                StatusCode = 500,
                ReasonPhrase = "Internal Server Error",
            };
        }

        // Build response pseudo-headers and headers
        var responseHeaders = new List<(string, string)> { (":status", response.StatusCode.ToString()) };

        // Map response headers — skip connection-specific fields
        foreach (var header in response.Headers)
        {
            var keyLower = header.Key.ToLowerInvariant();
            if (keyLower is "connection"
                or "transfer-encoding"
                or "keep-alive"
                or "proxy-connection"
                or "upgrade")
            {
                continue;
            }

            responseHeaders.Add((header.Key, header.Value));
        }

        // Encode response headers as HPACK block
        var hpackBlock = EncodeResponseHeaders(responseHeaders);

        // Determine HEADERS flags
        var headersFlags = Http2FrameFlags.EndHeaders;
        var hasBody = response.Body.Length > 0;

        if (!hasBody)
        {
            headersFlags |= Http2FrameFlags.EndStream;
        }

        // Send HEADERS frame
        var headersFrame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            headersFlags,
            SingleStreamId,
            hpackBlock
        );

        await connection.SendAsync(new ReadOnlySequence<byte>(headersFrame), ct);

        // Send DATA frame if body present
        if (hasBody)
        {
            var dataFrame = Http2FrameCodec.EncodeFrame(
                Http2FrameType.Data,
                Http2FrameFlags.EndStream,
                SingleStreamId,
                response.Body.Span
            );

            await connection.SendAsync(new ReadOnlySequence<byte>(dataFrame), ct);
        }

        return false; // keep connection alive
    }

    // ── Minimal HPACK encoder (no Huffman, literal-without-indexing only) ──

    /// <summary>
    /// Encodes a list of (name, value) header pairs into an HPACK block.
    /// Uses Literal Header Field without Indexing for every header.
    /// No Huffman encoding (acceptable for MVP; ~20% header size overhead).
    /// </summary>
    internal static byte[] EncodeResponseHeaders(List<(string Name, string Value)> headers)
    {
        var output = new List<byte>();
        foreach (var (name, value) in headers)
        {
            // Literal Header Field without Indexing — new name (index 0)
            //  0   1   2   3   4   5   6   7
            // +---+---+---+---+---+---+---+---+
            // | 0 | 0 | 0 | 0 |       0      |
            // +---+---+-----------------------+
            output.Add(0x00);

            // Name string (raw, no Huffman)
            EncodeRawString(output, name);

            // Value string (raw, no Huffman)
            EncodeRawString(output, value);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Encodes a string using the HPACK string format:
    /// H | Value Length (7+) | Value String
    /// H=0 (no Huffman), 7-bit prefix integer for length.
    /// </summary>
    private static void EncodeRawString(List<byte> output, string value)
    {
        var rawBytes = Encoding.ASCII.GetBytes(value);
        var length = rawBytes.Length;

        // 7-bit prefix integer encoding (RFC 7541 §5.1)
        if (length < 127)
        {
            // Fits in 7-bit prefix; MSB=0 means raw (no Huffman)
            output.Add((byte)length);
        }
        else
        {
            // Prefix all-1s signals continuation
            output.Add(0x7F);
            var remaining = length - 127;

            while (remaining >= 128)
            {
                // MSB=1 (more bytes follow), lower 7 bits = data
                output.Add((byte)((remaining & 0x7F) | 0x80));
                remaining >>= 7;
            }

            // Final byte: MSB=0
            output.Add((byte)remaining);
        }

        if (length > 0)
        {
            output.AddRange(rawBytes);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async ValueTask SendGoAwayAndCloseAsync(
        ITcpConnectionContext connection,
        Http2ErrorCode errorCode,
        CancellationToken ct
    )
    {
        var frame = Http2FrameCodec.EncodeGoAway(0, errorCode);
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct);
        connection.Close();
    }
}
