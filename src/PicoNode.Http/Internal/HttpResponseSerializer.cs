namespace PicoNode.Http.Internal;

internal static class HttpResponseSerializer
{
    private const string ContentLengthHeaderName = "Content-Length";
    private const string TransferEncodingHeaderName = "Transfer-Encoding";
    private const string ConnectionHeaderName = "Connection";
    private const string ServerHeaderName = "Server";
    private const string CloseConnectionHeaderValue = "close";
    private const string ChunkedHeaderValue = "chunked";
    private static readonly Encoding HeaderEncoding = Encoding.ASCII;
    private static readonly byte[] ChunkTerminatorBytes = "0\r\n\r\n"u8.ToArray();
    private static readonly byte[] CrLfBytes = "\r\n"u8.ToArray();

    public static ReadOnlySequence<byte> Serialize(
        HttpResponse response,
        bool closeConnection = false,
        string? serverHeader = null
    )
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateResponse(response, serverHeader);

        var headerBuffer = new ArrayBufferWriter<byte>(
            EstimateHeaderLength(response, closeConnection, serverHeader)
        );

        WriteStatusAndHeaders(
            headerBuffer,
            response,
            closeConnection,
            serverHeader,
            isChunked: false
        );
        WriteHeader(headerBuffer, ContentLengthHeaderName, response.Body.Length);
        WriteCrlf(headerBuffer);

        if (response.Body.IsEmpty)
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
        WriteStatusAndHeaders(
            headerBuffer,
            response,
            closeConnection,
            serverHeader,
            isChunked: true
        );
        WriteHeader(headerBuffer, TransferEncodingHeaderName, ChunkedHeaderValue);
        WriteCrlf(headerBuffer);

        return new ReadOnlySequence<byte>(headerBuffer.WrittenMemory);
    }

    public static ReadOnlySequence<byte> FormatChunk(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        var prefix = FormatChunkPrefix(data.Length);

        var first = new BufferSegment(prefix);
        var dataSegment = first.Append(data);
        var last = dataSegment.Append(CrLfBytes);

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static byte[] FormatChunkPrefix(int length)
    {
        Span<byte> hex = stackalloc byte[10];
        length.TryFormat(hex, out var written, "x");
        var prefix = new byte[written + 2];
        hex[..written].CopyTo(prefix);
        prefix[written] = (byte)'\r';
        prefix[written + 1] = (byte)'\n';
        return prefix;
    }

    public static ReadOnlySequence<byte> ChunkTerminator => new(ChunkTerminatorBytes);

    private static void ValidateResponse(HttpResponse response, string? serverHeader)
    {
        ValidateStatusLinePart(response.Version, nameof(response.Version));
        ValidateStatusLinePart(response.ReasonPhrase, nameof(response.ReasonPhrase));
        ValidateOptionalHeaderValue(serverHeader, nameof(serverHeader));

        foreach (var header in response.Headers)
        {
            ValidateHeaderName(header.Key);
            ValidateHeaderValue(header.Value, header.Key);
        }
    }

    private static void WriteStatusAndHeaders(
        ArrayBufferWriter<byte> buffer,
        HttpResponse response,
        bool closeConnection,
        string? serverHeader,
        bool isChunked
    )
    {
        WriteAscii(buffer, response.Version);
        WriteAscii(buffer, " ");
        WriteInt(buffer, response.StatusCode);
        WriteAscii(buffer, " ");
        WriteAscii(buffer, response.ReasonPhrase);
        WriteCrlf(buffer);

        foreach (var header in response.Headers)
        {
            if (header.Key.Equals(ContentLengthHeaderName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (header.Key.Equals(ConnectionHeaderName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (
                isChunked
                && header.Key.Equals(TransferEncodingHeaderName, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (
                !string.IsNullOrEmpty(serverHeader)
                && header.Key.Equals(ServerHeaderName, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            WriteHeader(buffer, header.Key, header.Value);
        }

        if (!string.IsNullOrEmpty(serverHeader))
        {
            WriteHeader(buffer, ServerHeaderName, serverHeader);
        }

        if (closeConnection)
        {
            WriteHeader(buffer, ConnectionHeaderName, CloseConnectionHeaderValue);
        }
    }

    private static int EstimateHeaderLength(
        HttpResponse response,
        bool closeConnection,
        string? serverHeader
    )
    {
        var length = response.Version.Length + response.ReasonPhrase.Length + 32;

        foreach (var header in response.Headers)
        {
            if (ShouldSkipApplicationHeader(header.Key, serverHeader))
            {
                continue;
            }

            length += header.Key.Length + header.Value.Length + 4;
        }

        if (!string.IsNullOrEmpty(serverHeader))
        {
            length += ServerHeaderName.Length + serverHeader.Length + 4;
        }

        if (closeConnection)
        {
            length += ConnectionHeaderName.Length + CloseConnectionHeaderValue.Length + 4;
        }

        length += ContentLengthHeaderName.Length + CountDigits(response.Body.Length) + 4;
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

    private static bool ShouldSkipApplicationHeader(string name, string? serverHeader) =>
        name.Equals(ContentLengthHeaderName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(ConnectionHeaderName, StringComparison.OrdinalIgnoreCase)
        || (
            !string.IsNullOrEmpty(serverHeader)
            && name.Equals(ServerHeaderName, StringComparison.OrdinalIgnoreCase)
        );

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
        WriteAscii(buffer, value);
        WriteCrlf(buffer);
    }

    private static void WriteHeader(ArrayBufferWriter<byte> buffer, string name, int value)
    {
        WriteAscii(buffer, name);
        WriteAscii(buffer, ": ");
        WriteInt(buffer, value);
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
            var next = new BufferSegment(memory) { RunningIndex = RunningIndex + Memory.Length, };

            Next = next;
            return next;
        }
    }
}
