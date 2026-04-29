namespace PicoNode.Http.Internal.HttpRequestParsing;

internal static class HttpHeaderParser
{
    public static HttpRequestParser.HeaderParseState ParseHeaders(
        ref HttpRequestParser.BufferReader reader,
        HttpConnectionHandlerOptions options,
        string version
    )
    {
        var headerFields = new List<KeyValuePair<string, string>>(capacity: 16);
        var headers = new Dictionary<string, string>(
            capacity: 16,
            StringComparer.OrdinalIgnoreCase
        );
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
                    ? HttpRequestParser.HeaderParseState.Incomplete()
                    : HttpRequestParser.HeaderParseState.Rejected(error.Value);
            }

            if (headerLineBytes.Length == 0)
            {
                break;
            }

            if (!TryParseHeaderLine(headerLineBytes, out var name, out var value))
            {
                return HttpRequestParser
                    .HeaderParseState
                    .Rejected(HttpRequestParseError.InvalidHeader);
            }

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    return HttpRequestParser
                        .HeaderParseState
                        .Rejected(HttpRequestParseError.UnsupportedFraming);
                }

                isChunked = true;
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (hasContentLength)
                {
                    return HttpRequestParser
                        .HeaderParseState
                        .Rejected(HttpRequestParseError.DuplicateContentLength);
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
                    return HttpRequestParser
                        .HeaderParseState
                        .Rejected(HttpRequestParseError.InvalidContentLength);
                }

                hasContentLength = true;
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (hasHost || !HostValidator.IsValidHostHeaderValue(value))
                {
                    return HttpRequestParser
                        .HeaderParseState
                        .Rejected(HttpRequestParseError.InvalidHostHeader);
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
            return HttpRequestParser
                .HeaderParseState
                .Rejected(HttpRequestParseError.MissingHostHeader);
        }

        if (isChunked && hasContentLength)
        {
            return HttpRequestParser
                .HeaderParseState
                .Rejected(HttpRequestParseError.InvalidRequestLine);
        }

        var expectsContinue =
            version == "HTTP/1.1"
            && headers.TryGetValue("Expect", out var expectValue)
            && expectValue.Equals("100-continue", StringComparison.OrdinalIgnoreCase);

        return HttpRequestParser
            .HeaderParseState
            .Success(headerFields, headers, contentLength, isChunked, expectsContinue);
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
        var valueSpan = HttpParseHelpers.TrimOws(line[(colonIndex + 1)..]);

        if (!HttpCharacters.IsHttpToken(nameSpan) || !HttpCharacters.IsValidHeaderValue(valueSpan))
        {
            return false;
        }

        name = Encoding.ASCII.GetString(nameSpan);
        value = Encoding.ASCII.GetString(valueSpan);
        return true;
    }
}
