namespace PicoNode.Http.Internal;

internal static class HttpResponseSerializer
{
    private const string CloseConnectionHeaderValue = "close";
    private const string ChunkedHeaderValue = "chunked";
    private static readonly Encoding HeaderEncoding = Encoding.ASCII;
    private static readonly byte[] ChunkTerminatorBytes = "0\r\n\r\n"u8.ToArray();

    public static ReadOnlySequence<byte> Serialize(
        HttpResponse response,
        bool closeConnection = false,
        string? serverHeader = null,
        bool omitBody = false
    )
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateResponse(response, serverHeader);

        var headerBuffer = new ArrayBufferWriter<byte>(
            EstimateHeaderLength(response, closeConnection, serverHeader)
        );

        WriteStatusAndHeaders(headerBuffer, response, closeConnection, serverHeader);
        WriteContentLength(headerBuffer, response, omitBody);
        WriteCrlf(headerBuffer);

        if (omitBody || response.Body.IsEmpty || !HasContent(response.StatusCode))
        {
            return new ReadOnlySequence<byte>(headerBuffer.WrittenMemory);
        }

        var firstSegment = new BufferSegment(headerBuffer.WrittenMemory);
        var lastSegment = firstSegment.Append(response.Body);
        return new ReadOnlySequence<byte>(firstSegment, 0, lastSegment, lastSegment.Memory.Length);
    }

    public static ReadOnlySequence<byte> SerializeChunkedHeaders(
        HttpResponse response,
        bool closeConnection = false,
        string? serverHeader = null
    )
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateResponse(response, serverHeader);

        var headerBuffer = new ArrayBufferWriter<byte>(256);
        WriteStatusAndHeaders(headerBuffer, response, closeConnection, serverHeader);
        WriteHeader(headerBuffer, HttpHeaderNames.TransferEncoding, ChunkedHeaderValue);
        WriteCrlf(headerBuffer);

        return new ReadOnlySequence<byte>(headerBuffer.WrittenMemory);
    }

    /// <summary>
    /// Longest chunk header any chunk can need: an <see cref="int"/> renders as at most 8 hex
    /// digits, plus the CRLF — which is why <see cref="AllocateStreamingBuffer"/> adds it.
    /// </summary>
    internal const int MaxChunkPrefixLength = 10;

    /// <summary>The CRLF that closes a chunk, written after its payload.</summary>
    internal const int ChunkCrLfLength = 2;

    /// <summary>
    /// Rents the buffer a streaming response needs: the chunk-header region (offset 0), the
    /// payload the reader fills (offset <see cref="MaxChunkPrefixLength"/>), and the CRLF that
    /// closes the chunk. The caller owns the array and must return it to
    /// <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    internal static byte[] AllocateStreamingBuffer(int readSize) =>
        ArrayPool<byte>.Shared.Rent(MaxChunkPrefixLength + readSize + ChunkCrLfLength);

    /// <summary>
    /// Writes the chunk header of a <paramref name="dataLength"/>-octet chunk into the *end* of
    /// <paramref name="headerRegion"/>, so the payload can follow it in the same buffer and the
    /// whole chunk leaves in one segment. Returns the header length; the payload is expected at
    /// <c>headerRegion.Length</c>, so the header starts at
    /// <c>headerRegion.Length - returnValue</c>.
    /// </summary>
    /// <remarks>
    /// This replaces a three-segment <see cref="ReadOnlySequence{T}"/> plus a freshly allocated
    /// prefix array per chunk: framing a chunk now costs no allocation at all.
    /// </remarks>
    internal static int WriteChunkPrefix(Span<byte> headerRegion, int dataLength)
    {
        // A zero-length chunk *is* the body terminator, not a data chunk: writing a
        // header for one here would end the stream in the middle of the body.
        ArgumentOutOfRangeException.ThrowIfLessThan(dataLength, 1);

        Span<byte> hex = stackalloc byte[8];
        dataLength.TryFormat(hex, out var written, "x");

        var prefixLength = written + 2;
        if (headerRegion.Length < prefixLength)
        {
            throw new ArgumentException(
                $"The chunk header region must hold {prefixLength} bytes.",
                nameof(headerRegion)
            );
        }

        var start = headerRegion.Length - prefixLength;
        hex[..written].CopyTo(headerRegion[start..]);
        headerRegion[start + written] = (byte)'\r';
        headerRegion[start + written + 1] = (byte)'\n';
        return prefixLength;
    }

    public static ReadOnlySequence<byte> ChunkTerminator => new(ChunkTerminatorBytes);

    private static void ValidateResponse(HttpResponse response, string? serverHeader)
    {
        ValidateStatusLinePart(response.ReasonPhrase, nameof(response.ReasonPhrase));
        ValidateOptionalHeaderValue(serverHeader, nameof(serverHeader));

        foreach (var header in response.Headers)
        {
            ValidateHeaderName(header.Key);
            ValidateHeaderValue(header.Value, header.Key);
        }
    }

    /// <summary>
    /// Writes the status line plus every application header the serializer does not
    /// own. Two names are never copied: <c>Content-Length</c> (recomputed by
    /// <see cref="WriteContentLength"/>, except for the 304 echo) and
    /// <c>Transfer-Encoding</c>.
    /// </summary>
    /// <remarks>
    /// <c>Transfer-Encoding</c> is dropped unconditionally — not only on the chunked
    /// path — because framing belongs to the serializer. RFC 9112 §6.1/§6.3 forbid one
    /// message from carrying both <c>Transfer-Encoding</c> and <c>Content-Length</c>,
    /// and <see cref="Serialize"/> always announces a body it sends raw with a
    /// <c>Content-Length</c>: an application-supplied <c>Transfer-Encoding: chunked</c>
    /// would make a downstream proxy parse that raw body as chunk framing (response
    /// smuggling). The chunked path writes its own single
    /// <c>Transfer-Encoding: chunked</c>.
    /// </remarks>
    private static void WriteStatusAndHeaders(
        ArrayBufferWriter<byte> buffer,
        HttpResponse response,
        bool closeConnection,
        string? serverHeader
    )
    {
        WriteAscii(buffer, VersionToString(response.Version));
        WriteAscii(buffer, " ");
        WriteInt(buffer, response.StatusCode);
        WriteAscii(buffer, " ");
        WriteAscii(buffer, response.ReasonPhrase);
        WriteCrlf(buffer);

        foreach (var header in response.Headers)
        {
            if (
                header.Key.Equals(HttpHeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (
                closeConnection
                && header.Key.Equals(HttpHeaderNames.Connection, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (
                header.Key.Equals(
                    HttpHeaderNames.TransferEncoding,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;
            if (
                !string.IsNullOrEmpty(serverHeader)
                && header.Key.Equals(HttpHeaderNames.Server, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            WriteHeader(buffer, header.Key, header.Value);
        }

        if (!string.IsNullOrEmpty(serverHeader))
        {
            WriteHeader(buffer, HttpHeaderNames.Server, serverHeader);
        }

        if (closeConnection)
        {
            WriteHeader(buffer, HttpHeaderNames.Connection, CloseConnectionHeaderValue);
        }
    }

    /// <summary>
    /// Emits the framing header this response is allowed to carry (RFC 9110 §8.6,
    /// RFC 9112 §6.3). The status code decides:
    /// <list type="bullet">
    /// <item>1xx and 204 — nothing: <see cref="HasContent"/> is false and
    /// <c>Content-Length</c> is forbidden outright.</item>
    /// <item>304 — an application-supplied value only. RFC 9110 §15.4.5 lets a 304
    /// repeat the <c>Content-Length</c> a 200 would have carried; a synthesized
    /// <c>0</c> (or the length of whatever body the handler built) claims a length the
    /// 200 never had.</item>
    /// <item>everything else — the serializer's own framing: the in-memory body length,
    /// or for HEAD the application-declared entity length (RFC 9110 §9.3.2), falling
    /// back to the body length.</item>
    /// </list>
    /// </summary>
    private static void WriteContentLength(
        ArrayBufferWriter<byte> buffer,
        HttpResponse response,
        bool omitBody
    )
    {
        var statusCode = response.StatusCode;

        if (!HasContent(statusCode))
        {
            if (statusCode == 304 && TryGetDeclaredContentLength(response, out var declared304))
            {
                WriteHeader(buffer, HttpHeaderNames.ContentLength, declared304);
            }

            return;
        }

        // HEAD (RFC 9110 §9.3.2): identical metadata to the GET response, no payload.
        // An application-provided Content-Length describes the entity a GET would
        // return (e.g. static files) and is preserved; otherwise the length of the
        // in-memory body is used.
        if (omitBody && TryGetDeclaredContentLength(response, out var declaredHead))
        {
            WriteHeader(buffer, HttpHeaderNames.ContentLength, declaredHead);
            return;
        }

        WriteHeader(buffer, HttpHeaderNames.ContentLength, response.Body.Length);
    }

    private static bool TryGetDeclaredContentLength(HttpResponse response, out long length)
    {
        length = 0;

        return response.Headers.TryGetValue(HttpHeaderNames.ContentLength, out var declared)
            && long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out length)
            && length >= 0;
    }

    /// <summary>
    /// RFC 9110 §6.4.1: a 1xx, 204 or 304 response has no content. Sending a body
    /// anyway would leave it unframed — no <c>Content-Length</c> is allowed to
    /// announce it — so on a keep-alive connection the next response would start in
    /// the middle of those octets.
    /// </summary>
    private static bool HasContent(int statusCode) =>
        !(statusCode is >= 100 and < 200 or 204 or 304);

    /// <summary>
    /// Sizing hint for the header buffer only — <see cref="ArrayBufferWriter{T}"/> grows
    /// on demand, so an estimate that is a few bytes off costs nothing but a reallocation.
    /// </summary>
    private static int EstimateHeaderLength(
        HttpResponse response,
        bool closeConnection,
        string? serverHeader
    )
    {
        var length = HttpVersionStringLength + response.ReasonPhrase.Length + 32;

        foreach (var header in response.Headers)
        {
            if (ShouldSkipApplicationHeader(header.Key, serverHeader, closeConnection))
            {
                continue;
            }

            length += header.Key.Length + header.Value.Length + 4;
        }

        if (!string.IsNullOrEmpty(serverHeader))
        {
            length += HttpHeaderNames.Server.Length + serverHeader.Length + 4;
        }

        if (closeConnection)
        {
            length += HttpHeaderNames.Connection.Length + CloseConnectionHeaderValue.Length + 4;
        }

        length += HttpHeaderNames.ContentLength.Length + CountDigits(response.Body.Length) + 4;
        return length + 2;
    }

    private static int CountDigits(int value)
    {
        var digits = 1;

        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }

    private static bool ShouldSkipApplicationHeader(
        string name,
        string? serverHeader,
        bool closeConnection = false
    ) =>
        name.Equals(HttpHeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HttpHeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase)
        || (
            closeConnection
            && name.Equals(HttpHeaderNames.Connection, StringComparison.OrdinalIgnoreCase)
        )
        || (
            !string.IsNullOrEmpty(serverHeader)
            && name.Equals(HttpHeaderNames.Server, StringComparison.OrdinalIgnoreCase)
        );

    private static string VersionToString(HttpVersion version) =>
        version switch
        {
            HttpVersion.Http10 => "HTTP/1.0",
            HttpVersion.Http11 => "HTTP/1.1",
            HttpVersion.Http2 => "HTTP/2",
            _ => "HTTP/1.1",
        };

    private const int HttpVersionStringLength = 8; // longest: "HTTP/1.0" / "HTTP/1.1"

    private static void ValidateStatusLinePart(string value, string paramName)
    {
        if (ContainsInvalidHeaderText(value))
        {
            throw new ArgumentException(
                "HTTP status metadata cannot contain CR or LF characters.",
                paramName
            );
        }
    }

    private static void ValidateHeaderName(string value)
    {
        if (!HttpCharacters.IsHttpToken(value))
        {
            throw new ArgumentException(
                "HTTP header names must be valid tokens.",
                nameof(HttpResponse.Headers)
            );
        }
    }

    private static void ValidateHeaderValue(string value, string headerName)
    {
        if (!HttpCharacters.IsValidHeaderValue(value))
        {
            throw new ArgumentException(
                $"HTTP header '{headerName}' contains invalid characters.",
                nameof(HttpResponse.Headers)
            );
        }
    }

    private static void ValidateOptionalHeaderValue(string? value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (!HttpCharacters.IsValidHeaderValue(value))
        {
            throw new ArgumentException(
                "HTTP header values contain invalid characters.",
                paramName
            );
        }
    }

    private static bool ContainsInvalidHeaderText(string value) =>
        value.Contains('\r') || value.Contains('\n');

    private static void WriteHeader(ArrayBufferWriter<byte> buffer, string name, string value)
    {
        WriteAscii(buffer, name);
        WriteAscii(buffer, ": ");
        WriteUtf8(buffer, value);
        WriteCrlf(buffer);
    }

    private static void WriteHeader(ArrayBufferWriter<byte> buffer, string name, int value)
    {
        WriteAscii(buffer, name);
        WriteAscii(buffer, ": ");
        WriteInt(buffer, value);
        WriteCrlf(buffer);
    }

    private static void WriteHeader(ArrayBufferWriter<byte> buffer, string name, long value)
    {
        WriteAscii(buffer, name);
        WriteAscii(buffer, ": ");
        WriteUtf8(buffer, value.ToString(CultureInfo.InvariantCulture));
        WriteCrlf(buffer);
    }

    private static void WriteAscii(IBufferWriter<byte> buffer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytesNeeded = HeaderEncoding.GetByteCount(value);
        var destination = buffer.GetSpan(bytesNeeded);
        var written = HeaderEncoding.GetBytes(value, destination);
        buffer.Advance(written);
    }

    private static readonly Encoding HeaderValueEncoding = new UTF8Encoding(false); // no BOM

    private static void WriteUtf8(IBufferWriter<byte> buffer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytesNeeded = HeaderValueEncoding.GetByteCount(value);
        var destination = buffer.GetSpan(bytesNeeded);
        var written = HeaderValueEncoding.GetBytes(value, destination);
        buffer.Advance(written);
    }

    private static void WriteInt(IBufferWriter<byte> buffer, int value)
    {
        var span = buffer.GetSpan(11);
        value.TryFormat(span, out var bytesWritten);
        buffer.Advance(bytesWritten);
    }

    private static void WriteCrlf(IBufferWriter<byte> buffer)
    {
        var span = buffer.GetSpan(2);
        span[0] = (byte)'\r';
        span[1] = (byte)'\n';
        buffer.Advance(2);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new BufferSegment(memory) { RunningIndex = RunningIndex + Memory.Length };

            Next = next;
            return next;
        }
    }
}
