using System.Globalization;
using System.Text;

namespace PicoNode.Http.Internal;

internal enum HttpRequestParseStatus
{
    Incomplete,
    Success,
    Rejected,
}

internal enum HttpRequestParseError
{
    InvalidRequestLine,
    InvalidHeader,
    InvalidHostHeader,
    MissingHostHeader,
    DuplicateContentLength,
    InvalidContentLength,
    UnsupportedFraming,
    RequestTooLarge,
    InvalidChunkedBody,
}

internal readonly record struct HttpRequestParseResult(
    HttpRequestParseStatus Status,
    HttpRequest? Request,
    SequencePosition Consumed,
    HttpRequestParseError? Error,
    bool ExpectsContinue = false
)
{
    public static HttpRequestParseResult Incomplete(
        SequencePosition consumed,
        bool expectsContinue = false
    ) => new(HttpRequestParseStatus.Incomplete, null, consumed, null, expectsContinue);

    public static HttpRequestParseResult Success(HttpRequest request, SequencePosition consumed) =>
        new(HttpRequestParseStatus.Success, request, consumed, null);

    public static HttpRequestParseResult Rejected(
        SequencePosition consumed,
        HttpRequestParseError error
    ) => new(HttpRequestParseStatus.Rejected, null, consumed, error);
}

internal static class HttpRequestParser
{
    private static readonly SearchValues<byte> InvalidTargetBytes = SearchValues.Create(
        [(byte)' ', (byte)'\t', (byte)'\r', (byte)'\n', (byte)'#']
    );

    public static HttpRequestParseResult Parse(
        ReadOnlySequence<byte> buffer,
        HttpConnectionHandlerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRequestBytes <= 0)
        {
            return HttpRequestParseResult.Rejected(
                buffer.Start,
                HttpRequestParseError.RequestTooLarge
            );
        }

        var reader = new BufferReader(buffer);

        if (
            !reader.TryReadLine(
                options.MaxRequestBytes,
                HttpRequestParseError.InvalidRequestLine,
                out var requestLineBytes,
                out var error
            )
        )
        {
            return error is null
                ? HttpRequestParseResult.Incomplete(buffer.Start)
                : HttpRequestParseResult.Rejected(buffer.Start, error.Value);
        }

        if (!TryParseRequestLine(requestLineBytes, out var method, out var target, out var version))
        {
            return HttpRequestParseResult.Rejected(
                buffer.Start,
                HttpRequestParseError.InvalidRequestLine
            );
        }

        var headerResult = ParseHeaders(ref reader, options, version);

        if (headerResult.IsIncomplete)
        {
            return HttpRequestParseResult.Incomplete(buffer.Start);
        }

        if (headerResult.Error is { } headerError)
        {
            return HttpRequestParseResult.Rejected(buffer.Start, headerError);
        }

        if (headerResult.IsChunked)
        {
            return ParseChunkedBody(
                ref reader,
                options,
                method,
                target,
                version,
                headerResult.HeaderFields,
                headerResult.Headers,
                headerResult.ExpectsContinue
            );
        }

        return ParseBody(
            ref reader,
            options,
            method,
            target,
            version,
            headerResult.HeaderFields,
            headerResult.Headers,
            headerResult.ContentLength,
            headerResult.ExpectsContinue
        );
    }

    private ref struct BufferReader
    {
        private readonly ReadOnlySequence<byte> _buffer;

        public BufferReader(ReadOnlySequence<byte> buffer)
        {
            _buffer = buffer;
            Position = buffer.Start;
            ConsumedBytes = 0;
        }

        public SequencePosition Position;
        public long ConsumedBytes;

        public SequencePosition BufferStart => _buffer.Start;

        public ReadOnlySequence<byte> SliceFromPosition() => _buffer.Slice(Position);

        public void Advance(long count)
        {
            Position = _buffer.GetPosition(count, Position);
        }

        public bool TryReadLine(
            int maxRequestBytes,
            HttpRequestParseError malformedLineError,
            out byte[] lineBytes,
            out HttpRequestParseError? error
        )
        {
            var remaining = _buffer.Slice(Position);
            var newline = remaining.PositionOf((byte)'\n');
            if (newline is null)
            {
                if (ConsumedBytes + remaining.Length > maxRequestBytes)
                {
                    lineBytes =  [];
                    error = HttpRequestParseError.RequestTooLarge;
                    return false;
                }

                lineBytes =  [];
                error = null;
                return false;
            }

            var lineWithCr = remaining.Slice(0, newline.Value);
            var lineLength = (int)lineWithCr.Length;

            if (lineLength == 0 || GetLastByte(lineWithCr) != (byte)'\r')
            {
                lineBytes =  [];
                error = malformedLineError;
                return false;
            }

            var contentLength = lineLength - 1;
            ConsumedBytes += lineLength + 1;
            if (ConsumedBytes > maxRequestBytes)
            {
                lineBytes =  [];
                error = HttpRequestParseError.RequestTooLarge;
                return false;
            }

            Position = _buffer.GetPosition(lineLength + 1, Position);
            error = null;

            if (contentLength == 0)
            {
                lineBytes =  [];
                return true;
            }

            lineBytes = new byte[contentLength];
            lineWithCr.Slice(0, contentLength).CopyTo(lineBytes);
            return true;
        }
    }

    private readonly record struct HeaderParseState(
        List<KeyValuePair<string, string>> HeaderFields,
        Dictionary<string, string> Headers,
        long ContentLength,
        bool IsChunked,
        bool ExpectsContinue,
        HttpRequestParseError? Error,
        bool IsIncomplete
    );

    private static HeaderParseState ParseHeaders(
        ref BufferReader reader,
        HttpConnectionHandlerOptions options,
        string version
    )
    {
        var headerFields = new List<KeyValuePair<string, string>>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentLength = 0L;
        var hasContentLength = false;
        var hasHost = false;
        var isChunked = false;

        while (true)
        {
            if (
                !reader.TryReadLine(
                    options.MaxRequestBytes,
                    HttpRequestParseError.InvalidHeader,
                    out var headerLineBytes,
                    out var error
                )
            )
            {
                return error is null
                    ? new HeaderParseState(
                        headerFields,
                        headers,
                        contentLength,
                        false,
                        false,
                        null,
                        true
                    )
                    : new HeaderParseState(
                        headerFields,
                        headers,
                        contentLength,
                        false,
                        false,
                        error,
                        false
                    );
            }

            if (headerLineBytes.Length == 0)
            {
                break;
            }

            if (!TryParseHeaderLine(headerLineBytes, out var name, out var value))
            {
                return new HeaderParseState(
                    headerFields,
                    headers,
                    contentLength,
                    false,
                    false,
                    HttpRequestParseError.InvalidHeader,
                    false
                );
            }

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    return new HeaderParseState(
                        headerFields,
                        headers,
                        contentLength,
                        false,
                        false,
                        HttpRequestParseError.UnsupportedFraming,
                        false
                    );
                }

                isChunked = true;
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (hasContentLength)
                {
                    return new HeaderParseState(
                        headerFields,
                        headers,
                        contentLength,
                        false,
                        false,
                        HttpRequestParseError.DuplicateContentLength,
                        false
                    );
                }

                if (
                    !long.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out contentLength
                    )
                    || contentLength < 0
                )
                {
                    return new HeaderParseState(
                        headerFields,
                        headers,
                        contentLength,
                        false,
                        false,
                        HttpRequestParseError.InvalidContentLength,
                        false
                    );
                }

                hasContentLength = true;
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (hasHost)
                {
                    return new HeaderParseState(
                        headerFields,
                        headers,
                        contentLength,
                        false,
                        false,
                        HttpRequestParseError.InvalidHostHeader,
                        false
                    );
                }

                if (!HostValidator.IsValidHostHeaderValue(value))
                {
                    return new HeaderParseState(
                        headerFields,
                        headers,
                        contentLength,
                        false,
                        false,
                        HttpRequestParseError.InvalidHostHeader,
                        false
                    );
                }

                hasHost = true;
            }

            headerFields.Add(new KeyValuePair<string, string>(name, value));

            if (!headers.TryAdd(name, value))
            {
                headers[name] = headers[name] + ", " + value;
            }
        }

        if (!hasHost && version == "HTTP/1.1")
        {
            return new HeaderParseState(
                headerFields,
                headers,
                contentLength,
                false,
                false,
                HttpRequestParseError.MissingHostHeader,
                false
            );
        }

        if (isChunked && hasContentLength)
        {
            return new HeaderParseState(
                headerFields,
                headers,
                contentLength,
                false,
                false,
                HttpRequestParseError.InvalidRequestLine,
                false
            );
        }

        var expectsContinue =
            version == "HTTP/1.1"
            && headers.TryGetValue("Expect", out var expectValue)
            && expectValue.Equals("100-continue", StringComparison.OrdinalIgnoreCase);

        return new HeaderParseState(
            headerFields,
            headers,
            contentLength,
            isChunked,
            expectsContinue,
            null,
            false
        );
    }

    private static HttpRequestParseResult ParseBody(
        ref BufferReader reader,
        HttpConnectionHandlerOptions options,
        string method,
        string target,
        string version,
        List<KeyValuePair<string, string>> headerFields,
        Dictionary<string, string> headers,
        long contentLength,
        bool expectsContinue
    )
    {
        var body = ReadOnlyMemory<byte>.Empty;

        if (contentLength > 0)
        {
            if (
                contentLength > int.MaxValue
                || reader.ConsumedBytes + contentLength > options.MaxRequestBytes
            )
            {
                return HttpRequestParseResult.Rejected(
                    reader.BufferStart,
                    HttpRequestParseError.RequestTooLarge
                );
            }

            var bodyBytes = reader.SliceFromPosition();
            if (bodyBytes.Length < contentLength)
            {
                return HttpRequestParseResult.Incomplete(reader.BufferStart, expectsContinue);
            }

            var bodyArray = new byte[contentLength];
            bodyBytes.Slice(0, contentLength).CopyTo(bodyArray);
            reader.Advance(contentLength);
            body = bodyArray;
        }

        return HttpRequestParseResult.Success(
            new HttpRequest
            {
                Method = method,
                Target = target,
                Version = version,
                HeaderFields = headerFields,
                Headers = headers,
                Body = body,
            },
            reader.Position
        );
    }

    private static HttpRequestParseResult ParseChunkedBody(
        ref BufferReader reader,
        HttpConnectionHandlerOptions options,
        string method,
        string target,
        string version,
        List<KeyValuePair<string, string>> headerFields,
        Dictionary<string, string> headers,
        bool expectsContinue
    )
    {
        var bodyParts = new List<byte[]>();
        var totalBodyLength = 0L;

        while (true)
        {
            if (
                !reader.TryReadLine(
                    options.MaxRequestBytes,
                    HttpRequestParseError.InvalidChunkedBody,
                    out var chunkSizeLine,
                    out var error
                )
            )
            {
                return error is null
                    ? HttpRequestParseResult.Incomplete(reader.BufferStart, expectsContinue)
                    : HttpRequestParseResult.Rejected(reader.BufferStart, error.Value);
            }

            var chunkSize = ParseChunkSize(chunkSizeLine);
            if (chunkSize < 0)
            {
                return HttpRequestParseResult.Rejected(
                    reader.BufferStart,
                    HttpRequestParseError.InvalidChunkedBody
                );
            }

            if (chunkSize == 0)
            {
                while (true)
                {
                    if (
                        !reader.TryReadLine(
                            options.MaxRequestBytes,
                            HttpRequestParseError.InvalidChunkedBody,
                            out var trailerLine,
                            out var trailerError
                        )
                    )
                    {
                        return trailerError is null
                            ? HttpRequestParseResult.Incomplete(reader.BufferStart, expectsContinue)
                            : HttpRequestParseResult.Rejected(
                                reader.BufferStart,
                                trailerError.Value
                            );
                    }

                    if (trailerLine.Length == 0)
                    {
                        break;
                    }
                }

                break;
            }

            totalBodyLength += chunkSize;
            if (
                totalBodyLength > int.MaxValue
                || reader.ConsumedBytes + totalBodyLength > options.MaxRequestBytes
            )
            {
                return HttpRequestParseResult.Rejected(
                    reader.BufferStart,
                    HttpRequestParseError.RequestTooLarge
                );
            }

            var remaining = reader.SliceFromPosition();
            if (remaining.Length < chunkSize + 2)
            {
                return HttpRequestParseResult.Incomplete(reader.BufferStart, expectsContinue);
            }

            var crlfStart = remaining.Slice(chunkSize, 2);
            if (GetFirstByte(crlfStart) != (byte)'\r' || GetLastByte(crlfStart) != (byte)'\n')
            {
                return HttpRequestParseResult.Rejected(
                    reader.BufferStart,
                    HttpRequestParseError.InvalidChunkedBody
                );
            }

            var chunkData = new byte[chunkSize];
            remaining.Slice(0, chunkSize).CopyTo(chunkData);
            bodyParts.Add(chunkData);

            reader.Advance(chunkSize + 2);
            reader.ConsumedBytes += chunkSize + 2;
        }

        var body = new byte[totalBodyLength];
        var offset = 0;
        foreach (var part in bodyParts)
        {
            part.CopyTo(body, offset);
            offset += part.Length;
        }

        return HttpRequestParseResult.Success(
            new HttpRequest
            {
                Method = method,
                Target = target,
                Version = version,
                HeaderFields = headerFields,
                Headers = headers,
                Body = body,
            },
            reader.Position
        );
    }

    private static int ParseChunkSize(ReadOnlySpan<byte> line)
    {
        if (line.Length == 0)
        {
            return -1;
        }

        var semiColon = line.IndexOf((byte)';');
        var hexPart = semiColon >= 0 ? line[..semiColon] : line;

        if (hexPart.Length == 0)
        {
            return -1;
        }

        var size = 0;
        foreach (var b in hexPart)
        {
            if (!IsHexDigit(b))
            {
                return -1;
            }

            size = (size << 4) | HexValue(b);
            if (size < 0)
            {
                return -1;
            }
        }

        return size;
    }

    private static int HexValue(byte b) =>
        b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
            >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
            >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
            _ => -1,
        };

    private static bool TryParseRequestLine(
        ReadOnlySpan<byte> line,
        out string method,
        out string target,
        out string version
    )
    {
        method = string.Empty;
        target = string.Empty;
        version = string.Empty;

        var firstSpace = line.IndexOf((byte)' ');
        if (firstSpace <= 0)
        {
            return false;
        }

        var rest = line[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf((byte)' ');
        if (secondSpace <= 0)
        {
            return false;
        }

        var versionSpan = rest[(secondSpace + 1)..];
        if (versionSpan.IndexOf((byte)' ') >= 0)
        {
            return false;
        }

        var methodSpan = line[..firstSpace];
        var targetSpan = rest[..secondSpace];

        if (!HttpCharacters.IsHttpToken(methodSpan) || !IsRequestTarget(targetSpan))
        {
            return false;
        }

        if (versionSpan.SequenceEqual("HTTP/1.1"u8))
        {
            version = "HTTP/1.1";
        }
        else if (versionSpan.SequenceEqual("HTTP/1.0"u8))
        {
            version = "HTTP/1.0";
        }
        else
        {
            return false;
        }

        method = Encoding.ASCII.GetString(methodSpan);
        target = Encoding.ASCII.GetString(targetSpan);
        return true;
    }

    private static bool TryParseHeaderLine(
        ReadOnlySpan<byte> line,
        out string name,
        out string value
    )
    {
        name = string.Empty;
        value = string.Empty;

        var colonIndex = line.IndexOf((byte)':');
        if (colonIndex <= 0)
        {
            return false;
        }

        var nameSpan = line[..colonIndex];
        var valueSpan = TrimOws(line[(colonIndex + 1)..]);

        if (!HttpCharacters.IsHttpToken(nameSpan) || !HttpCharacters.IsValidHeaderValue(valueSpan))
        {
            return false;
        }

        name = Encoding.ASCII.GetString(nameSpan);
        value = Encoding.ASCII.GetString(valueSpan);
        return true;
    }

    private static ReadOnlySpan<byte> TrimOws(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsOws(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsOws(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsOws(byte b) => b is (byte)' ' or (byte)'\t';

    private static bool IsRequestTarget(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || value[0] != (byte)'/')
        {
            return false;
        }

        if (value.IndexOfAny(InvalidTargetBytes) >= 0)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != (byte)'%')
            {
                continue;
            }

            if (
                index + 2 >= value.Length
                || !IsHexDigit(value[index + 1])
                || !IsHexDigit(value[index + 2])
            )
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool IsHexDigit(byte b) =>
        b
            is >= (byte)'0'
                and <= (byte)'9'
                or >= (byte)'A'
                and <= (byte)'F'
                or >= (byte)'a'
                and <= (byte)'f';

    private static byte GetFirstByte(ReadOnlySequence<byte> sequence) => sequence.FirstSpan[0];

    private static byte GetLastByte(ReadOnlySequence<byte> sequence) =>
        sequence.Slice(sequence.Length - 1).FirstSpan[0];
}
