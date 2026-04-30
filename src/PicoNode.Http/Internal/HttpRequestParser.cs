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

        if (
            !HttpRequestLineParser.TryParse(
                requestLineBytes,
                out var method,
                out var target,
                out var path,
                out var queryString,
                out var version
            )
        )
        {
            return HttpRequestParseResult.Rejected(
                buffer.Start,
                HttpRequestParseError.InvalidRequestLine
            );
        }

        var headerResult = HttpHeaderParser.ParseHeaders(ref reader, options, version);

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
            return HttpBodyParser.ParseChunkedBody(
                ref reader,
                options,
                method,
                target,
                path,
                queryString,
                version,
                headerResult.HeaderFields,
                headerResult.Headers,
                headerResult.ExpectsContinue
            );
        }

        return HttpBodyParser.ParseBody(
            ref reader,
            options,
            method,
            target,
            path,
            queryString,
            version,
            headerResult.HeaderFields,
            headerResult.Headers,
            headerResult.ContentLength,
            headerResult.ExpectsContinue
        );
    }

    internal ref struct BufferReader(ReadOnlySequence<byte> buffer)
    {
        public SequencePosition Position = buffer.Start;
        public long ConsumedBytes = 0;

        public SequencePosition BufferStart => buffer.Start;

        public ReadOnlySequence<byte> SliceFromPosition() => buffer.Slice(Position);

        public void Advance(long count)
        {
            Position = buffer.GetPosition(count, Position);
        }

        public bool TryReadLine(
            int maxRequestBytes,
            HttpRequestParseError malformedLineError,
            out ReadOnlySpan<byte> lineBytes,
            out HttpRequestParseError? error
        )
        {
            var remaining = buffer.Slice(Position);
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

            if (lineLength == 0 || HttpParseHelpers.GetLastByte(lineWithCr) != (byte)'\r')
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

            Position = buffer.GetPosition(lineLength + 1, Position);
            error = null;

            if (contentLength == 0)
            {
                lineBytes =  [];
                return true;
            }

            lineBytes = lineWithCr.FirstSpan.Slice(0, contentLength);
            return true;
        }
    }

    internal readonly record struct HeaderParseState
    {
        public List<KeyValuePair<string, string>> HeaderFields { get; init; }
        public Dictionary<string, string> Headers { get; init; }
        public long ContentLength { get; init; }
        public bool IsChunked { get; init; }
        public bool ExpectsContinue { get; init; }
        public HttpRequestParseError? Error { get; init; }
        public bool IsIncomplete { get; init; }

        public static HeaderParseState Incomplete() => new() { IsIncomplete = true };

        public static HeaderParseState Rejected(HttpRequestParseError error) =>
            new() { Error = error };

        public static HeaderParseState Success(
            List<KeyValuePair<string, string>> headerFields,
            Dictionary<string, string> headers,
            long contentLength,
            bool isChunked,
            bool expectsContinue
        ) =>
            new()
            {
                HeaderFields = headerFields,
                Headers = headers,
                ContentLength = contentLength,
                IsChunked = isChunked,
                ExpectsContinue = expectsContinue,
            };
    };
}
