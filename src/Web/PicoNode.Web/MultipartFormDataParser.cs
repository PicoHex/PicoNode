namespace PicoNode.Web;

/// <summary>
/// Parses multipart/form-data request bodies.
/// <para>
/// Strategy: if <c>request.Body</c> has data (in-memory buffer), parses directly.
/// Otherwise, delegates to <see cref="Internal.StreamingMultipartParser"/> for
/// streaming reads from <c>request.BodyStream</c> (large payloads without buffering).
/// </para>
/// </summary>
public static class MultipartFormDataParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private static ReadOnlySpan<byte> CrLf => "\r\n"u8;
    private static ReadOnlySpan<byte> DoubleCrLf => "\r\n\r\n"u8;
    private static ReadOnlySpan<byte> DashDash => "--"u8;

    public static async ValueTask<MultipartFormData?> ParseAsync(
        HttpRequest request,
        MultipartFormDataParserOptions? options = null,
        ILogger? logger = null,
        CancellationToken ct = default
    )
    {
        options ??= new MultipartFormDataParserOptions();
        var boundary = ResolveBoundary(request, options);
        if (boundary is null)
            return null;

        if (request.Body.Length > 0)
        {
            var boundaryBytes = Encoding.UTF8.GetBytes(boundary);
            return ParseBody(request.Body, boundaryBytes, options, logger);
        }

        var bodyStream = request.BodyStream;
        if (bodyStream != Stream.Null)
        {
            return await StreamingMultipartParser
                .ParseAsync(
                    bodyStream,
                    boundary,
                    maxPartSizeBytes: options.MaxPartSizeBytes,
                    maxTotalSizeBytes: options.MaxTotalSizeBytes,
                    maxPartCount: options.MaxPartCount,
                    maxHeaderBytesPerPart: options.MaxHeaderBytesPerPart,
                    ct: ct
                )
                .ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Validates content type and extracts boundary string. Returns null on failure.</summary>
    private static string? ResolveBoundary(
        HttpRequest request,
        MultipartFormDataParserOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBoundaryLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.MaxBoundaryLength,
            MultipartFormDataParserOptions.DefaultMaxBoundaryLength
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPartCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxHeaderBytesPerPart, 1);

        var contentType = GetHeaderValue(request.HeaderFields, HttpHeaderNames.ContentType);
        if (contentType is null)
            return null;

        if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return null;

        return ExtractBoundary(contentType, options.MaxBoundaryLength);
    }

    internal static string? ExtractBoundary(
        string contentType,
        int maxBoundaryLength = MultipartFormDataParserOptions.DefaultMaxBoundaryLength
    )
    {
        var span = contentType.AsSpan();
        var idx = span.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var boundary = ExtractValue(span[(idx + 9)..], [';', ' ']);
        return IsValidBoundary(boundary, maxBoundaryLength) ? boundary : null;
    }

    private static MultipartFormData ParseBody(
        ReadOnlyMemory<byte> body,
        byte[] boundary,
        MultipartFormDataParserOptions options,
        ILogger? logger = null
    )
    {
        var bodySpan = body.Span;
        var fields = new List<MultipartFormField>();
        var files = new List<MultipartFormFile>();
        var totalBytes = 0L;
        var partCount = 0;

        var delimiter = new byte[boundary.Length + 2];
        DashDash.CopyTo(delimiter.AsSpan());
        boundary.CopyTo(delimiter.AsSpan(2));

        var pos = FindBoundary(bodySpan, delimiter, searchStart: 0, requireLeadingCrLf: false);
        if (pos < 0)
            return new MultipartFormData(fields, files);

        pos += delimiter.Length;

        while (pos < bodySpan.Length)
        {
            if (pos + CrLf.Length <= bodySpan.Length && bodySpan[pos..].StartsWith(DashDash))
                break;

            if (pos + CrLf.Length <= bodySpan.Length && bodySpan[pos..].StartsWith(CrLf))
                pos += CrLf.Length;
            else
                break;

            var headerEnd = IndexOf(bodySpan[pos..], DoubleCrLf);
            if (headerEnd < 0)
                break;

            var headerBytes = bodySpan[pos..(pos + headerEnd)];
            pos += headerEnd + DoubleCrLf.Length;

            // The header block as it stands on the wire: every header line together with
            // the CRLF that ends it (the empty line that closes the block is not counted).
            // MultipartPartReader charges the same bytes on the streaming path, so a body
            // at the limit is accepted — or refused — by both paths alike.
            var headerLength = headerEnd + CrLf.Length;

            // Same limits as the streaming path, in the same order: part count, the
            // per-part header guard, then the per-part content guard, with the running
            // total (headers + content) checked after each addition
            // (StreamingMultipartParser / MultipartPartReader semantics).
            partCount++;
            if (partCount > options.MaxPartCount)
            {
                throw new InvalidDataException("Multipart body exceeds maximum part count");
            }

            if (headerLength > options.MaxHeaderBytesPerPart)
            {
                throw new InvalidDataException("Multipart part headers exceed maximum size");
            }

            totalBytes += headerLength;
            if (totalBytes > options.MaxTotalSizeBytes)
            {
                throw new InvalidDataException("Multipart body exceeds maximum total size");
            }

            var nextDelimiter = FindBoundary(
                bodySpan,
                delimiter,
                searchStart: pos,
                requireLeadingCrLf: true
            );
            if (nextDelimiter < 0)
                break;

            var contentEnd = nextDelimiter - CrLf.Length;
            if (contentEnd < pos)
                break;

            var content = body[pos..contentEnd];
            pos = nextDelimiter + delimiter.Length;

            if (content.Length > options.MaxPartSizeBytes)
            {
                throw new InvalidDataException("Multipart part exceeds maximum size");
            }

            totalBytes += content.Length;
            if (totalBytes > options.MaxTotalSizeBytes)
            {
                throw new InvalidDataException("Multipart body exceeds maximum total size");
            }

            ParsePart(headerBytes, content, fields, files, logger);
        }

        return new MultipartFormData(fields, files);
    }

    private static void ParsePart(
        ReadOnlySpan<byte> headerBytes,
        ReadOnlyMemory<byte> content,
        List<MultipartFormField> fields,
        List<MultipartFormFile> files,
        ILogger? logger = null
    )
    {
        var headers = DecodeUtf8(headerBytes, logger);
        if (headers is null)
            return;

        var disposition = GetPartHeaderValue(headers, "Content-Disposition");
        if (disposition is null)
            return;

        var name = ContentDispositionHeader.GetParameter(disposition, "name");
        if (name is null)
            return;

        var fileName = ContentDispositionHeader.GetParameter(disposition, "filename");

        if (fileName is not null)
        {
            var ct =
                GetPartHeaderValue(headers, HttpHeaderNames.ContentType)
                ?? "application/octet-stream";
            files.Add(new MultipartFormFile(name, fileName, ct, content));
        }
        else
        {
            var fieldValue = DecodeUtf8(content.Span, logger);
            if (fieldValue is null)
                return;

            fields.Add(new MultipartFormField(name, fieldValue));
        }
    }

    private static string? ExtractValue(ReadOnlySpan<char> value, ReadOnlySpan<char> terminators)
    {
        if (value.Length >= 2 && value[0] == '"')
        {
            var endQuote = value[1..].IndexOf('"');
            return endQuote < 0 ? null : value[1..(endQuote + 1)].ToString();
        }

        var end = value.IndexOfAny(terminators);
        return (end >= 0 ? value[..end] : value).ToString();
    }

    private static string? GetPartHeaderValue(string headers, string headerName)
    {
        var remaining = headers.AsSpan();

        while (remaining.Length > 0)
        {
            var lineEnd = remaining.IndexOf('\n');
            ReadOnlySpan<char> line;

            if (lineEnd >= 0)
            {
                var end = lineEnd > 0 && remaining[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                line = remaining[..end];
                remaining = remaining[(lineEnd + 1)..];
            }
            else
            {
                line = remaining;
                remaining = [];
            }

            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0)
                continue;

            var key = line[..colonIdx].Trim();
            if (!key.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                continue;

            return line[(colonIdx + 1)..].Trim().ToString();
        }

        return null;
    }

    private static string? GetHeaderValue(
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string name
    )
    {
        return (
            from header in headers
            where header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
            select header.Value
        ).FirstOrDefault();
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.IndexOf(needle);
    }

    private static int FindBoundary(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> delimiter,
        int searchStart,
        bool requireLeadingCrLf
    )
    {
        var searchOffset = searchStart;
        while (searchOffset < body.Length)
        {
            var relativeIndex = IndexOf(body[searchOffset..], delimiter);
            if (relativeIndex < 0)
                return -1;

            var boundaryStart = searchOffset + relativeIndex;
            if (requireLeadingCrLf)
            {
                if (
                    boundaryStart < CrLf.Length
                    || !body[(boundaryStart - CrLf.Length)..boundaryStart].SequenceEqual(CrLf)
                )
                {
                    searchOffset = boundaryStart + 1;
                    continue;
                }
            }

            var boundaryEnd = boundaryStart + delimiter.Length;
            if (boundaryEnd > body.Length)
                return -1;

            if (IsBoundaryTerminator(body[boundaryEnd..]))
                return boundaryStart;

            searchOffset = boundaryStart + 1;
        }

        return -1;
    }

    private static bool IsBoundaryTerminator(ReadOnlySpan<byte> remaining)
    {
        if (remaining.StartsWith(CrLf))
            return true;

        return remaining.StartsWith(DashDash);
    }

    private static string? DecodeUtf8(ReadOnlySpan<byte> value, ILogger? logger = null)
    {
        try
        {
            return StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException ex)
        {
            logger?.Log(
                LogLevel.Debug,
                new EventId(0),
                "Decoder fallback in multipart parsing",
                ex
            );
            return null;
        }
    }

    private static bool IsValidBoundary(string? boundary, int maxBoundaryLength)
    {
        if (string.IsNullOrEmpty(boundary) || boundary.Length > maxBoundaryLength)
        {
            return false;
        }

        foreach (var ch in boundary)
        {
            if (
                ch <= 0x20
                || ch >= 0x7F
                || ch
                    is '"'
                        or '('
                        or ')'
                        or ','
                        or '/'
                        or ':'
                        or ';'
                        or '<'
                        or '='
                        or '>'
                        or '?'
                        or '@'
                        or '['
                        or '\\'
                        or ']'
            )
            {
                return false;
            }
        }

        return true;
    }
}
