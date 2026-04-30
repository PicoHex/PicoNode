namespace PicoNode.Http.Internal.HttpRequestParsing;

internal static class HttpHeaderParser
{
    public static HttpRequestParser.HeaderParseState ParseHeaders(
        ref HttpRequestParser.BufferReader reader,
        HttpConnectionHandlerOptions options,
        HttpVersion version
    )
    {
        var headerFields = new List<KeyValuePair<string, string>>(capacity: 16);
        // Single-value dictionary for the common case (no repeated headers).
        // A separate multiValues dictionary is allocated only when a duplicate
        // header is detected, avoiding per-header List<string> allocations.
        var headers = new Dictionary<string, string>(
            capacity: 16,
            StringComparer.OrdinalIgnoreCase
        );
        Dictionary<string, List<string>>? multiValues = null;
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

            if (name.Equals(HttpHeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    return HttpRequestParser
                        .HeaderParseState
                        .Rejected(HttpRequestParseError.UnsupportedFraming);
                }

                isChunked = true;
            }

            if (name.Equals(HttpHeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase))
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

            if (name.Equals(HttpHeaderNames.Host, StringComparison.OrdinalIgnoreCase))
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

            // Fast path: first occurrence → store as single string value (no allocation beyond the dict entry).
            if (!headers.TryAdd(name, value))
            {
                // Duplicate header — promote to multi-value list.
                multiValues ??= new Dictionary<string, List<string>>(
                    StringComparer.OrdinalIgnoreCase
                );

                if (!multiValues.TryGetValue(name, out var values))
                {
                    values = new List<string>(capacity: 2) { headers[name] };
                    multiValues[name] = values;
                }
                values.Add(value);
            }
        }

        // Flatten multi-value headers back into the single-value dictionary.
        // Single-value headers pass through with zero allocation.
        if (multiValues is not null)
        {
            foreach (var (headerName, values) in multiValues)
            {
                headers[headerName] = string.Join(", ", values);
            }
        }

        if (!hasHost && version == HttpVersion.Http11)
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
            version == HttpVersion.Http11
            && headers.TryGetValue(HttpHeaderNames.Expect, out var expectValue)
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
