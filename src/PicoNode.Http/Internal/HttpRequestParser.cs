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
            out var requestLine,
            out var error
        ))
        {
            return error is null ? HttpRequestParseResult.Incomplete(buffer.Start) : HttpRequestParseResult.Rejected(buffer.Start, error.Value);
        }

        if (!TryParseRequestLine(requestLine, out var method, out var target, out var version))
        {
            return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.InvalidRequestLine);
        }

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
                out var headerLine,
                out error
            ))
            {
                return error is null ? HttpRequestParseResult.Incomplete(buffer.Start) : HttpRequestParseResult.Rejected(buffer.Start, error.Value);
            }

            if (headerLine.Length == 0)
            {
                break;
            }

            if (!TryParseHeaderLine(headerLine, out var name, out var value))
            {
                return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.InvalidHeader);
            }

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.UnsupportedFraming);
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (hasContentLength)
                {
                    return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.DuplicateContentLength);
                }

                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
                {
                    return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.InvalidContentLength);
                }

                hasContentLength = true;
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (hasHost)
                {
                    return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.InvalidHostHeader);
                }

                if (!IsValidHostHeaderValue(value))
                {
                    return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.InvalidHostHeader);
                }

                hasHost = true;
            }

            headerFields.Add(new KeyValuePair<string, string>(name, value));

            if (!headers.TryAdd(name, value))
            {
                headers[name] = headers[name] + ", " + value;
            }
        }

        if (!headers.ContainsKey("Host"))
        {
            return HttpRequestParseResult.Rejected(buffer.Start, HttpRequestParseError.MissingHostHeader);
        }

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

            var body = new byte[contentLength];
            bodyBytes.Slice(0, contentLength).CopyTo(body);
            position = buffer.GetPosition(contentLength, position);

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

        return HttpRequestParseResult.Success(
            new HttpRequest
            {
                Method = method,
                Target = target,
                Version = version,
                HeaderFields = headerFields,
                Headers = headers,
                Body = ReadOnlyMemory<byte>.Empty,
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
        out string line,
        out HttpRequestParseError? error
    )
    {
        var remaining = buffer.Slice(position);
        var newline = remaining.PositionOf((byte)'\n');
        if (newline is null)
        {
            if (consumedBytes + remaining.Length > maxRequestBytes)
            {
                line = string.Empty;
                error = HttpRequestParseError.RequestTooLarge;
                return false;
            }

            line = string.Empty;
            error = null;
            return false;
        }

        var lineWithCarriageReturn = remaining.Slice(0, newline.Value);
        var lineBytes = ToArray(lineWithCarriageReturn);
        if (lineBytes.Length == 0 || lineBytes[^1] != '\r')
        {
            line = string.Empty;
            error = malformedLineError;
            return false;
        }

        line = Encoding.ASCII.GetString(lineBytes, 0, lineBytes.Length - 1);
        consumedBytes += lineBytes.Length + 1;
        if (consumedBytes > maxRequestBytes)
        {
            error = HttpRequestParseError.RequestTooLarge;
            return false;
        }

        position = buffer.GetPosition(lineBytes.Length + 1, position);
        error = null;
        return true;
    }

    private static bool TryParseRequestLine(string requestLine, out string method, out string target, out string version)
    {
        method = string.Empty;
        target = string.Empty;
        version = string.Empty;

        var firstSpace = requestLine.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return false;
        }

        var secondSpace = requestLine.IndexOf(' ', firstSpace + 1);
        if (secondSpace <= firstSpace + 1 || secondSpace != requestLine.LastIndexOf(' '))
        {
            return false;
        }

        method = requestLine[..firstSpace];
        target = requestLine[(firstSpace + 1)..secondSpace];
        version = requestLine[(secondSpace + 1)..];

        return IsHttpToken(method) && IsRequestTarget(target) && version == "HTTP/1.1";
    }

    private static bool TryParseHeaderLine(string headerLine, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;

        var colonIndex = headerLine.IndexOf(':');
        if (colonIndex <= 0)
        {
            return false;
        }

        name = headerLine[..colonIndex];
        value = headerLine[(colonIndex + 1)..].Trim();

        return IsHttpToken(name) && IsValidHeaderValue(value);
    }

    private static bool IsHttpToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsHttpTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRequestTarget(string value)
    {
        if (value.Length == 0 || value[0] != '/')
        {
            return false;
        }

        if (value.IndexOfAny([' ', '\t', '\r', '\n', '#']) >= 0)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
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

    private static bool IsValidHeaderValue(string value)
    {
        foreach (var character in value)
        {
            if ((character < 0x20 && character != '\t') || character == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidHostHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is ',' or '/' or '?' or '#' or '@')
            {
                return false;
            }
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (value[0] == '[')
        {
            return TryParseBracketedIpv6Host(value);
        }

        var lastColon = value.LastIndexOf(':');
        string hostPart;

        if (lastColon >= 0)
        {
            hostPart = value[..lastColon];
            var portPart = value[(lastColon + 1)..];

            if (hostPart.Length == 0 || !IsValidPort(portPart))
            {
                return false;
            }
        }
        else
        {
            hostPart = value;
        }

        return IsValidHostName(hostPart) || IsValidIpv4Address(hostPart);
    }

    private static bool IsHttpTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static bool TryParseBracketedIpv6Host(string value)
    {
        var closingBracketIndex = value.IndexOf(']');
        if (closingBracketIndex <= 1)
        {
            return false;
        }

        var addressPart = value[1..closingBracketIndex];
        if (!System.Net.IPAddress.TryParse(addressPart, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (closingBracketIndex == value.Length - 1)
        {
            return true;
        }

        if (value[closingBracketIndex + 1] != ':')
        {
            return false;
        }

        return IsValidPort(value[(closingBracketIndex + 2)..]);
    }

    private static bool IsValidPort(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 0 and <= 65535;
    }

    private static bool IsValidHostName(string value)
    {
        if (value.Length == 0 || value.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var labels = value.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0)
            {
                return false;
            }

            if (!char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
            {
                return false;
            }

            foreach (var character in label)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsValidIpv4Address(string value)
    {
        var segments = value.Split('.');
        if (segments.Length != 4)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return false;
            }

            if (!byte.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHexDigit(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static byte[] ToArray(ReadOnlySequence<byte> sequence)
    {
        var buffer = new byte[sequence.Length];
        sequence.CopyTo(buffer);
        return buffer;
    }
}
