namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static class Http2StreamHandler
{
    public static async ValueTask<bool> ProcessHeadersFrame(
        ITcpConnectionContext connection,
        Http2Frame frame,
        HttpRequestHandler requestHandler,
        ILogger? logger,
        CancellationToken ct
    )
    {
        // Get or create stream state, which handles CONTINUATION buffering.
        var state = GetStreamState(connection, frame.StreamId);

        // CONTINUATION buffering: if END_HEADERS is not set, buffer and return.
        ArraySegment<byte>? payloadData;
        try
        {
            payloadData = state.AppendHeaderData(
                frame.Payload,
                frame.HasFlag(Http2FrameFlags.EndHeaders)
            );
        }
        catch (Http2HeaderTooLargeException)
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.CompressionError, ct);
            return true;
        }

        if (payloadData is null)
        {
            // Headers not yet complete (waiting for CONTINUATION).
            // Save the EndStream flag from the HEADERS frame for later use.
            state.EndStreamFromHeaders = frame.HasFlag(Http2FrameFlags.EndStream);

            var runtimeState = connection.UserState as ConnectionRuntimeState;
            if (runtimeState is not null)
                runtimeState.PendingContinuationStreamId = frame.StreamId;
            return false;
        }

        // Clear pending CONTINUATION tracking — headers are now complete.
        var clearState = connection.UserState as ConnectionRuntimeState;
        if (clearState is not null)
            clearState.PendingContinuationStreamId = null;

        // Decode HPACK header block from the complete (possibly reassembled) data.
        if (!HpackDecoder.TryDecode(payloadData.Value.AsSpan(), out var headerFields))
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.CompressionError, ct);
            return true;
        }

        // Extract pseudo-headers and regular headers (needed for validation before deferral).
        string? method = null;
        string? path = null;
        string? scheme = null;
        string? authority = null;
        var regularHeaders = new List<KeyValuePair<string, string>>();
        var headerDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headerFields)
        {
            switch (name)
            {
                case ":method":
                    method = value;
                    break;
                case ":path":
                    path = value;
                    break;
                case ":scheme":
                    scheme = value;
                    break;
                case ":authority":
                    authority = value;
                    break;
                default:
                    regularHeaders.Add(new(name, value));
                    if (!headerDict.ContainsKey(name))
                        headerDict[name] = value;
                    break;
            }
        }

        // Validate required pseudo-headers before any deferral
        if (method is null || path is null)
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct);
            return true;
        }

        // If END_STREAM is not set, defer handler invocation and wait for DATA frames.
        // Use EndStreamFromHeaders for the CONTINUATION path (flag is on HEADERS, not CONTINUATION).
        var endStream = frame.HasFlag(Http2FrameFlags.EndStream) || state.EndStreamFromHeaders;
        if (!endStream)
        {
            StoreDecodedHeaders(state, method, path, regularHeaders, headerDict);
            state.EndStreamReceived = false;
            return false;
        }

        // Construct HttpRequest
        var request = new HttpRequest
        {
            Method = method,
            Target = path,
            Version = HttpVersion.Http11,
            HeaderFields = regularHeaders,
            Headers = headerDict,
        };

        // Invoke request handler
        HttpResponse response;
        try
        {
            response = await requestHandler(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Log(
                LogLevel.Error,
                new EventId(0),
                "Unhandled exception processing HTTP/2 stream",
                ex
            );

            response = new HttpResponse
            {
                StatusCode = 500,
                ReasonPhrase = "Internal Server Error",
            };
        }

        // Build response pseudo-headers and headers
        var responseHeaders = new List<(string, string)>
        {
            (":status", response.StatusCode.ToString()),
        };

        // Map response headers — skip connection-specific fields
        foreach (var header in response.Headers)
        {
            var keyLower = header.Key.ToLowerInvariant();
            if (
                keyLower
                is "connection"
                    or "transfer-encoding"
                    or "keep-alive"
                    or "proxy-connection"
                    or "upgrade"
            )
            {
                continue;
            }

            responseHeaders.Add((header.Key, header.Value));
        }

        // Encode response headers as HPACK block and send HEADERS frame in one allocation
        var hpackSize = MeasureResponseHeadersSize(responseHeaders);
        var headersFlags = Http2FrameFlags.EndHeaders;

        if (response.Body.Length == 0)
        {
            headersFlags |= Http2FrameFlags.EndStream;
            var frameRented = ArrayPool<byte>.Shared.Rent(
                Http2FrameCodec.FrameHeaderSize + hpackSize
            );
            try
            {
                Http2FrameCodec.WriteFrameHeader(
                    frameRented,
                    hpackSize,
                    Http2FrameType.Headers,
                    headersFlags,
                    frame.StreamId
                );
                WriteResponseHeaders(
                    frameRented.AsSpan(Http2FrameCodec.FrameHeaderSize),
                    responseHeaders
                );
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(
                        frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + hpackSize)
                    ),
                    ct
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameRented);
            }

            return false;
        }

        // Has body: send HEADERS frame first (no END_STREAM), then DATA frame
        {
            var frameRented = ArrayPool<byte>.Shared.Rent(
                Http2FrameCodec.FrameHeaderSize + hpackSize
            );
            try
            {
                Http2FrameCodec.WriteFrameHeader(
                    frameRented,
                    hpackSize,
                    Http2FrameType.Headers,
                    headersFlags,
                    frame.StreamId
                );
                WriteResponseHeaders(
                    frameRented.AsSpan(Http2FrameCodec.FrameHeaderSize),
                    responseHeaders
                );
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(
                        frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + hpackSize)
                    ),
                    ct
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameRented);
            }
        }

        // Send DATA frame with body
        {
            var bodySize = response.Body.Length;
            var frameRented = ArrayPool<byte>.Shared.Rent(
                Http2FrameCodec.FrameHeaderSize + bodySize
            );
            try
            {
                Http2FrameCodec.WriteFrame(
                    frameRented,
                    Http2FrameType.Data,
                    Http2FrameFlags.EndStream,
                    frame.StreamId,
                    response.Body.Span
                );
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(
                        frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + bodySize)
                    ),
                    ct
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameRented);
            }
        }

        return false;
    }

    // ── Minimal HPACK encoder (no Huffman, literal-without-indexing only) ──

    internal static int MeasureResponseHeadersSize(List<(string Name, string Value)> headers)
    {
        var size = 0;
        foreach (var (name, value) in headers)
        {
            size += 1; // literal header field without indexing prefix
            size += MeasureRawStringSize(name);
            size += MeasureRawStringSize(value);
        }
        return size;
    }

    internal static void WriteResponseHeaders(
        Span<byte> destination,
        List<(string Name, string Value)> headers
    )
    {
        var pos = 0;
        foreach (var (name, value) in headers)
        {
            destination[pos++] = 0x00;
            pos += WriteRawString(destination.Slice(pos), name);
            pos += WriteRawString(destination.Slice(pos), value);
        }
    }

    internal static byte[] EncodeResponseHeaders(List<(string Name, string Value)> headers)
    {
        var result = new byte[MeasureResponseHeadersSize(headers)];
        WriteResponseHeaders(result, headers);
        return result;
    }

    private static int MeasureRawStringSize(string value)
    {
        var rawLength = Encoding.ASCII.GetByteCount(value);
        var size = rawLength;
        if (rawLength < 127)
            size += 1;
        else
        {
            size += 1;
            var remaining = rawLength - 127;
            while (remaining >= 128)
            {
                size++;
                remaining >>= 7;
            }
            size++;
        }
        return size;
    }

    private static int WriteRawString(Span<byte> destination, string value)
    {
        var rawBytes = Encoding.ASCII.GetBytes(value);
        var length = rawBytes.Length;
        var pos = 0;

        if (length < 127)
        {
            destination[pos++] = (byte)length;
        }
        else
        {
            destination[pos++] = 0x7F;
            var remaining = length - 127;
            while (remaining >= 128)
            {
                destination[pos++] = (byte)((remaining & 0x7F) | 0x80);
                remaining >>= 7;
            }
            destination[pos++] = (byte)remaining;
        }

        if (length > 0)
        {
            rawBytes.CopyTo(destination.Slice(pos));
            pos += length;
        }

        return pos;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Http2StreamState GetStreamState(ITcpConnectionContext connection, int streamId)
    {
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
        {
            state = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };
            connection.UserState = state;
        }

        return state.GetOrCreateStream(streamId);
    }

    private static void StoreDecodedHeaders(
        Http2StreamState state,
        string method,
        string path,
        List<KeyValuePair<string, string>> regularHeaders,
        Dictionary<string, string> headerDict
    )
    {
        state.DecodedMethod = method;
        state.DecodedPath = path;
        state.DecodedHeaderFields = regularHeaders;
        state.DecodedHeadersDict = headerDict;
    }

    public static async ValueTask<bool> ProcessDataFrame(
        ITcpConnectionContext connection,
        Http2Frame frame,
        HttpRequestHandler requestHandler,
        ILogger? logger,
        CancellationToken ct
    )
    {
        // Validate stream exists
        var runtimeState = connection.UserState as ConnectionRuntimeState;
        if (runtimeState?.Http2Streams?.TryGetValue(frame.StreamId, out var state) != true
            || state is null)
        {
            return false;
        }

        // Buffer the data
        if (frame.Payload.Length > 0)
        {
            state.DataBuffer.Write(frame.Payload.Span);
        }

        // If END_STREAM, complete the request and invoke the handler
        if (frame.HasFlag(Http2FrameFlags.EndStream))
        {
            state.EndStreamReceived = true;
            return await CompleteDeferredRequest(
                connection, state, requestHandler, logger, ct);
        }

        return false;
    }

    private static async ValueTask<bool> CompleteDeferredRequest(
        ITcpConnectionContext connection,
        Http2StreamState state,
        HttpRequestHandler requestHandler,
        ILogger? logger,
        CancellationToken ct
    )
    {
        try
        {
            // Build request from stored headers and buffered data
            var bodyBytes = state.DataBuffer.WrittenMemory;
            var bodyStream = bodyBytes.Length > 0
                ? new ReadOnlySequenceStream(
                    new ReadOnlySequence<byte>(bodyBytes))
                : Stream.Null;

            var request = new HttpRequest
            {
                Method = state.DecodedMethod ?? "GET",
                Target = state.DecodedPath ?? "/",
                Path = state.DecodedPath ?? "/",
                Version = PicoNode.Http.HttpVersion.Http11,
                HeaderFields = state.DecodedHeaderFields ?? [],
                Headers = state.DecodedHeadersDict
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                BodyStream = bodyStream,
            };

            // Invoke handler
            var response = await requestHandler(request, ct);
            state.HandlerInvoked = true;

            // Send response
            await SendResponseAsync(
                connection, state, response, state.StreamId, logger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Log(LogLevel.Error, new EventId(0),
                "Unhandled exception processing HTTP/2 deferred stream", ex);

            var errorResponse = new HttpResponse
            {
                StatusCode = 500,
                ReasonPhrase = "Internal Server Error",
            };
            await SendResponseAsync(
                connection, state, errorResponse, state.StreamId, logger, ct);
        }

        return false;
    }

    private static async ValueTask SendResponseAsync(
        ITcpConnectionContext connection,
        Http2StreamState? state,
        HttpResponse response,
        int streamId,
        ILogger? logger,
        CancellationToken ct
    )
    {
        var responseHeaders = new List<(string, string)>
        {
            (":status", response.StatusCode.ToString()),
        };

        foreach (var header in response.Headers)
        {
            var keyLower = header.Key.ToLowerInvariant();
            if (keyLower is "connection" or "transfer-encoding"
                or "keep-alive" or "proxy-connection" or "upgrade")
                continue;
            responseHeaders.Add((header.Key, header.Value));
        }

        var hpackSize = MeasureResponseHeadersSize(responseHeaders);
        var headersFlags = Http2FrameFlags.EndHeaders;

        if (response.Body.Length == 0 && response.BodyStream is null)
        {
            headersFlags |= Http2FrameFlags.EndStream;
            var frameRented = ArrayPool<byte>.Shared.Rent(
                Http2FrameCodec.FrameHeaderSize + hpackSize);
            try
            {
                Http2FrameCodec.WriteFrameHeader(frameRented, hpackSize,
                    Http2FrameType.Headers, headersFlags, streamId);
                WriteResponseHeaders(
                    frameRented.AsSpan(Http2FrameCodec.FrameHeaderSize),
                    responseHeaders);
                await connection.SendAsync(new ReadOnlySequence<byte>(
                    frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + hpackSize)), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameRented);
            }
            return;
        }

        // Has body: HEADERS (no EndStream) + DATA (with EndStream)
        {
            var frameRented = ArrayPool<byte>.Shared.Rent(
                Http2FrameCodec.FrameHeaderSize + hpackSize);
            try
            {
                Http2FrameCodec.WriteFrameHeader(frameRented, hpackSize,
                    Http2FrameType.Headers, headersFlags, streamId);
                WriteResponseHeaders(
                    frameRented.AsSpan(Http2FrameCodec.FrameHeaderSize),
                    responseHeaders);
                await connection.SendAsync(new ReadOnlySequence<byte>(
                    frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + hpackSize)), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameRented);
            }
        }

        // Send DATA frame with body
        if (response.Body.Length > 0)
        {
            var bodySize = response.Body.Length;
            var frameRented = ArrayPool<byte>.Shared.Rent(
                Http2FrameCodec.FrameHeaderSize + bodySize);
            try
            {
                Http2FrameCodec.WriteFrame(frameRented, Http2FrameType.Data,
                    Http2FrameFlags.EndStream, streamId, response.Body.Span);
                await connection.SendAsync(new ReadOnlySequence<byte>(
                    frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + bodySize)), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameRented);
            }
        }

        if (state is not null)
            state.ResponseSent = true;
    }

    private static async ValueTask SendGoAwayAndCloseAsync(
        ITcpConnectionContext connection,
        Http2ErrorCode errorCode,
        CancellationToken ct
    )
    {
        var frame = Http2FrameCodec.EncodeGoAway(0, errorCode);
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct);
        connection.Close();
    }
}
