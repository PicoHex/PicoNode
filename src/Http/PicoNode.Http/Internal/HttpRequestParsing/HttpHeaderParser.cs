namespace PicoNode.Http.Internal.HttpRequestParsing;

internal static class HttpHeaderParser
{
    // Names that are not on HttpHeaderNames (it is public API, so this file adds no members to it) but
    // are frequent enough on the wire to be worth borrowing instead of materializing per request.
    private const string CookieName = "Cookie";
    private const string UserAgentName = "User-Agent";
    private const string AcceptName = "Accept";

    public static HttpRequestParser.HeaderParseState ParseHeaders(
        ref HttpRequestParser.BufferReader reader,
        HttpConnectionHandlerOptions options,
        HttpVersion version
    )
    {
        // One collection for both views: the lines in wire order (HeaderFields) and the name index
        // behind HeaderFields' dictionary twin (Headers).
        var headers = new HttpRequestHeaders();
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
                return HttpRequestParser.HeaderParseState.Rejected(
                    HttpRequestParseError.InvalidHeader
                );
            }

            if (name.Equals(HttpHeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    return HttpRequestParser.HeaderParseState.Rejected(
                        HttpRequestParseError.UnsupportedFraming
                    );
                }

                isChunked = true;
            }

            if (name.Equals(HttpHeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase))
            {
                if (
                    !long.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedLength
                    )
                    || parsedLength < 0
                )
                {
                    return HttpRequestParser.HeaderParseState.Rejected(
                        HttpRequestParseError.InvalidContentLength
                    );
                }

                // RFC 7230 §3.3.2: identical duplicate values are allowed;
                // conflicting values are a smuggling vector and must be rejected.
                if (hasContentLength && contentLength != parsedLength)
                {
                    return HttpRequestParser.HeaderParseState.Rejected(
                        HttpRequestParseError.DuplicateContentLength
                    );
                }

                contentLength = parsedLength;
                hasContentLength = true;
            }

            if (name.Equals(HttpHeaderNames.Host, StringComparison.OrdinalIgnoreCase))
            {
                if (hasHost || !HostValidator.IsValidHostHeaderValue(value))
                {
                    return HttpRequestParser.HeaderParseState.Rejected(
                        HttpRequestParseError.InvalidHostHeader
                    );
                }

                hasHost = true;
            }

            // Repeats are kept as separate lines and joined for the dictionary view on demand.
            headers.Add(name, value);
        }

        if (!hasHost && version == HttpVersion.Http11)
        {
            return HttpRequestParser.HeaderParseState.Rejected(
                HttpRequestParseError.MissingHostHeader
            );
        }

        if (isChunked && hasContentLength)
        {
            // TE+CL together is a framing violation (request smuggling vector),
            // not a malformed request line.
            return HttpRequestParser.HeaderParseState.Rejected(
                HttpRequestParseError.UnsupportedFraming
            );
        }

        var expectsContinue =
            version == HttpVersion.Http11
            && headers.TryGetValue(HttpHeaderNames.Expect, out var expectValue)
            && expectValue.Equals("100-continue", StringComparison.OrdinalIgnoreCase);

        return HttpRequestParser.HeaderParseState.Success(
            headers,
            contentLength,
            isChunked,
            expectsContinue
        );
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

        name = InternKnownName(nameSpan) ?? Encoding.ASCII.GetString(nameSpan);
        value = Encoding.ASCII.GetString(valueSpan);
        return true;
    }

    /// <summary>
    /// The shared instance of a well-known header name, or null when the name is not one of them — in
    /// which case the caller materializes it, because header names are not restricted to this list.
    /// </summary>
    /// <remarks>
    /// The match is byte-exact, casing included: a client that writes <c>accept</c> keeps <c>accept</c>
    /// in <see cref="HttpRequest.HeaderFields"/>, exactly as before interning existed. Only the spelling
    /// a client sends and a shared constant agree on is free of an allocation, and that is what real
    /// clients send. The comparison runs on the wire bytes, so no string is needed to look the name up.
    /// </remarks>
    private static string? InternKnownName(ReadOnlySpan<byte> name)
    {
        // Ordered by how often the name shows up in a request.
        if (name.SequenceEqual("Host"u8))
        {
            return HttpHeaderNames.Host;
        }

        if (name.SequenceEqual("User-Agent"u8))
        {
            return UserAgentName;
        }

        if (name.SequenceEqual("Accept"u8))
        {
            return AcceptName;
        }

        if (name.SequenceEqual("Accept-Encoding"u8))
        {
            return HttpHeaderNames.AcceptEncoding;
        }

        if (name.SequenceEqual("Connection"u8))
        {
            return HttpHeaderNames.Connection;
        }

        if (name.SequenceEqual("Cookie"u8))
        {
            return CookieName;
        }

        if (name.SequenceEqual("Content-Type"u8))
        {
            return HttpHeaderNames.ContentType;
        }

        if (name.SequenceEqual("Content-Length"u8))
        {
            return HttpHeaderNames.ContentLength;
        }

        if (name.SequenceEqual("Authorization"u8))
        {
            return HttpHeaderNames.Authorization;
        }

        if (name.SequenceEqual("Transfer-Encoding"u8))
        {
            return HttpHeaderNames.TransferEncoding;
        }

        return null;
    }
}
