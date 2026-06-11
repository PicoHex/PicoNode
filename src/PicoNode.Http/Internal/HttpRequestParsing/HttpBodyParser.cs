namespace PicoNode.Http.Internal.HttpRequestParsing;

internal static class HttpBodyParser
{
    public static HttpRequestParseResult ParseBody(
        ref HttpRequestParser.BufferReader reader,
        HttpConnectionHandlerOptions options,
        string method,
        string target,
        string path,
        string queryString,
        HttpVersion version,
        List<KeyValuePair<string, string>> headerFields,
        Dictionary<string, string> headers,
        long contentLength,
        bool expectsContinue
    )
    {
        if (contentLength <= 0)
            return HttpRequestParseResult.Success(
                new HttpRequest
                {
                    Method = method,
                    Target = target,
                    Path = path,
                    QueryString = queryString,
                    Version = version,
                    HeaderFields = headerFields,
                    Headers = headers,
                },
                reader.Position
            );

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

        // Create a stream over the remaining buffer data to enable streaming reads.
        // Also populate Body for backward compatibility with existing consumers.
        var bodySlice = bodyBytes.Slice(0, contentLength);
        reader.Advance(contentLength);

        // Body is populated from the stream data so both paths work.
        // Streaming consumers use BodyStream to avoid the extra allocation once
        // the non-streaming API is fully migrated.
        var bodyArray = bodySlice.ToArray();

        return HttpRequestParseResult.Success(
            new HttpRequest
            {
                Method = method,
                Target = target,
                Path = path,
                QueryString = queryString,
                Version = version,
                HeaderFields = headerFields,
                Headers = headers,
                BodyStream = new ReadOnlySequenceStream(bodySlice),
                Body = bodyArray,
            },
            reader.Position
        );
    }

    public static HttpRequestParseResult ParseChunkedBody(
        ref HttpRequestParser.BufferReader reader,
        HttpConnectionHandlerOptions options,
        string method,
        string target,
        string path,
        string queryString,
        HttpVersion version,
        List<KeyValuePair<string, string>> headerFields,
        Dictionary<string, string> headers,
        bool expectsContinue
    )
    {
        var bodyWriter = new ArrayBufferWriter<byte>();
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

            var chunkSize = HttpParseHelpers.ParseChunkSize(chunkSizeLine);
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
            if (remaining.Length < (long)chunkSize + 2)
            {
                return HttpRequestParseResult.Incomplete(reader.BufferStart, expectsContinue);
            }

            var crlfStart = remaining.Slice(chunkSize, 2);
            if (
                HttpParseHelpers.GetFirstByte(crlfStart) != (byte)'\r'
                || HttpParseHelpers.GetLastByte(crlfStart) != (byte)'\n'
            )
            {
                return HttpRequestParseResult.Rejected(
                    reader.BufferStart,
                    HttpRequestParseError.InvalidChunkedBody
                );
            }

            var span = bodyWriter.GetSpan(chunkSize);
            remaining.Slice(0, chunkSize).CopyTo(span);
            bodyWriter.Advance(chunkSize);

            reader.Advance(chunkSize + 2);
            reader.ConsumedBytes += chunkSize + 2;
        }

        // Chunked body is still fully buffered in ArrayBufferWriter; can be streamed
        // in a future optimization.  For now the in-memory copy is kept for chunked.
        var body = bodyWriter.WrittenSpan.ToArray();

        return HttpRequestParseResult.Success(
            new HttpRequest
            {
                Method = method,
                Target = target,
                Path = path,
                QueryString = queryString,
                Version = version,
                HeaderFields = headerFields,
                Headers = headers,
                Body = body,
                BodyStream = new MemoryStream(body, writable: false),
            },
            reader.Position
        );
    }
}
