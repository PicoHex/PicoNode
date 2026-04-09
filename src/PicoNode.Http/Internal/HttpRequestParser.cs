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
}

internal readonly record struct HttpRequestParseResult(
    HttpRequestParseStatus Status,
    HttpRequest? Request,
    SequencePosition Consumed,
    HttpRequestParseError? Error
)
{
    public static HttpRequestParseResult Incomplete(SequencePosition consumed) =>
        new(HttpRequestParseStatus.Incomplete, null, consumed, null);

    public static HttpRequestParseResult Success(HttpRequest request, SequencePosition consumed) =>
        new(HttpRequestParseStatus.Success, request, consumed, null);

    public static HttpRequestParseResult Rejected(SequencePosition consumed, HttpRequestParseError error) =>
        new(HttpRequestParseStatus.Rejected, null, consumed, error);
}

internal static class HttpRequestParser
{
    private static readonly SearchValues<byte> InvalidTargetBytes =
        SearchValues.Create([(byte)' ', (byte)'\t', (byte)'\r', (byte)'\n', (byte)'#']);

    public static HttpRequestParseResult Parse(ReadOnlySequence<byte> buffer, HttpConnectionHandlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRequestBytes <= 0)
        {
            return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.RequestTooLarge);
        }

        var position = buffer.Start;
        var consumedBytes = 0L;

        if (!TryReadLine(
            buffer,
            ref position,
            ref consumedBytes,
            options.MaxRequestBytes,
            HttpRequestParseError.InvalidRequestLine,
            out var requestLineBytes,
            out var error
        ))
        {
            return error is null
                ? HttpRequestParseResult.Incomplete(buffer.Start)
                : HttpRequestParseResult.Rejected(buffer.Start, error.Value);
        }

        if (!TryParseRequestLine(requestLineBytes, out var method, out var target, out var version))
        {
            return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.InvalidRequestLine);
        }

        var headerResult = ParseHeaders(buffer, ref position, ref consumedBytes, options);

        if (headerResult.IsIncomplete)
        {
            return HttpRequestParseResult.Incomplete(buffer.Start);
        }

        if (headerResult.Error is { } headerError)
        {
            return HttpRequestParseResult.Rejected(buffer.Start, headerError);
        }

        return ParseBody(
            buffer,
            ref position,
            consumedBytes,
            options,
            method,
            target,
            version,
            headerResult.HeaderFields,
            headerResult.Headers,
            headerResult.ContentLength
        );
    }

    private readonly record struct HeaderParseState(
        List<KeyValuePair<string, string>> HeaderFields,
        Dictionary<string, string> Headers,
        long ContentLength,
        HttpRequestParseError? Error,
        bool IsIncomplete
    );

    private static HeaderParseState ParseHeaders(
        ReadOnlySequence<byte> buffer,
        ref SequencePosition position,
        ref long consumedBytes,
        HttpConnectionHandlerOptions options
    )
    {
        var headerFields = new List<KeyValuePair<string, string>>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentLength = 0L;
        var hasContentLength = false;
        var hasHost = false;

        while (true)
        {
            if (!TryReadLine(
                buffer,
                ref position,
                ref consumedBytes,
                options.MaxRequestBytes,
                HttpRequestParseError.InvalidHeader,
                out var headerLineBytes,
                out var error
            ))
            {
                return error is null
                    ? new HeaderParseState(headerFields, headers, contentLength, null, true)
                    : new HeaderParseState(headerFields, headers, contentLength, error, false);
            }

            if (headerLineBytes.Length == 0)
            {
                break;
            }

            if (!TryParseHeaderLine(headerLineBytes, out var name, out var value))
            {
                return new HeaderParseState(headerFields, headers, contentLength, HttpRequestParseError.InvalidHeader, false);
            }

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                return new HeaderParseState(headerFields, headers, contentLength, HttpRequestParseError.UnsupportedFraming, false);
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (hasContentLength)
                {
                    return new HeaderParseState(headerFields, headers, contentLength, HttpRequestParseError.DuplicateContentLength, false);
                }

                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
                {
                    return new HeaderParseState(headerFields, headers, contentLength, HttpRequestParseError.InvalidContentLength, false);
                }

                hasContentLength = true;
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (hasHost)
                {
                    return new HeaderParseState(headerFields, headers, contentLength, HttpRequestParseError.InvalidHostHeader, false);
                }

                if (!HostValidator.IsValidHostHeaderValue(value))
                {
                    return new HeaderParseState(headerFields, headers, contentLength, HttpRequestParseError.InvalidHostHeader, false);
                }

                hasHost = true;
            }

            headerFields.Add(new KeyValuePair<string, string>(name, value));

            if (!headers.TryAdd(name, value))
            {
                headers[name] = headers[name] + ", " + value;
            }
        }

        if (!hasHost)
        {
            return new HeaderParseState(headerFields, headers, contentLength, HttpRequestParseError.MissingHostHeader, false);
        }

        return new HeaderParseState(headerFields, headers, contentLength, null, false);
    }

    private static HttpRequestParseResult ParseBody(
        ReadOnlySequence<byte> buffer,
        ref SequencePosition position,
        long consumedBytes,
        HttpConnectionHandlerOptions options,
        string method,
        string target,
        string version,
        List<KeyValuePair<string, string>> headerFields,
        Dictionary<string, string> headers,
        long contentLength
    )
    {
        var body = ReadOnlyMemory<byte>.Empty;

        if (contentLength > 0)
        {
            if (contentLength > int.MaxValue || consumedBytes + contentLength > options.MaxRequestBytes)
            {
                return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.RequestTooLarge);
            }

            var bodyBytes = buffer.Slice(position);
            if (bodyBytes.Length < contentLength)
            {
                return HttpRequestParseResult.Incomplete(buffer.Start);
            }

            var bodyArray = new byte[contentLength];
            bodyBytes.Slice(0, contentLength).CopyTo(bodyArray);
            position = buffer.GetPosition(contentLength, position);
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
            position
        );
    }

    private static bool TryReadLine(
        ReadOnlySequence<byte> buffer,
        ref SequencePosition position,
        ref long consumedBytes,
        int maxRequestBytes,
        HttpRequestParseError malformedLineError,
        out byte[] lineBytes,
        out HttpRequestParseError? error
    )
    {
        var remaining = buffer.Slice(position);
        var newline = remaining.PositionOf((byte)'\n');
        if (newline is null)
        {
            if (consumedBytes + remaining.Length > maxRequestBytes)
            {
                lineBytes = [];
                error = HttpRequestParseError.RequestTooLarge;
                return false;
            }

            lineBytes = [];
            error = null;
            return false;
        }

        var lineWithCr = remaining.Slice(0, newline.Value);
        var lineLength = (int)lineWithCr.Length;

        if (lineLength == 0 || GetLastByte(lineWithCr) != (byte)'\r')
        {
            lineBytes = [];
            error = malformedLineError;
            return false;
        }

        var contentLength = lineLength - 1;
        consumedBytes += lineLength + 1;
        if (consumedBytes > maxRequestBytes)
        {
            lineBytes = [];
            error = HttpRequestParseError.RequestTooLarge;
            return false;
        }

        position = buffer.GetPosition(lineLength + 1, position);
        error = null;

        if (contentLength == 0)
        {
            lineBytes = [];
            return true;
        }

        lineBytes = new byte[contentLength];
        lineWithCr.Slice(0, contentLength).CopyTo(lineBytes);
        return true;
    }

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

        if (!HttpCharacters.IsHttpToken(methodSpan)
            || !IsRequestTarget(targetSpan)
            || !versionSpan.SequenceEqual("HTTP/1.1"u8))
        {
            return false;
        }

        method = Encoding.ASCII.GetString(methodSpan);
        target = Encoding.ASCII.GetString(targetSpan);
        version = "HTTP/1.1";
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

            if (index + 2 >= value.Length || !IsHexDigit(value[index + 1]) || !IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool IsHexDigit(byte b) =>
        b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static byte GetLastByte(ReadOnlySequence<byte> sequence) =>
        sequence.Slice(sequence.Length - 1).FirstSpan[0];
}
