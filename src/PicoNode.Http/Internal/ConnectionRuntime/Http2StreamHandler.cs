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
        // Check stream concurrency limit before creating stream state.
        var runtimeStateForLimit = connection.UserState as ConnectionRuntimeState;
        if (runtimeStateForLimit is not null && frame.StreamId != 0)
        {
            var streamCount = runtimeStateForLimit.Http2Streams?.Count ?? 0;
            if (streamCount >= runtimeStateForLimit.RemoteMaxConcurrentStreams)
            {
                // Per RFC 7540, refuse the specific stream with RST_STREAM,
                // not GoAway (which would close the entire connection).
                await SendRstStreamAsync(
                    connection,
                    frame.StreamId,
                    Http2ErrorCode.RefusedStream,
                    ct
                );
                return false;
            }
        }

        var state = GetStreamState(connection, frame.StreamId);
        if (state is null)
        {
            // Invalid stream ID (even, non-monotonic, or closed reuse)
            await SendRstStreamAsync(connection, frame.StreamId, Http2ErrorCode.ProtocolError, ct);
            return false;
        }

        // Update activity timestamp for timeout tracking
        state.LastActivityUtc = DateTime.UtcNow;

        // State machine: validate HEADERS is legal in current state
        if (!state.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.Headers, out _))
        {
            await SendRstStreamAsync(connection, frame.StreamId, Http2ErrorCode.ProtocolError, ct);
            return false;
        }

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
        var dynamicTable = runtimeStateForLimit?.HpackTable;
        if (!HpackDecoder.TryDecode(payloadData.Value.AsSpan(), out var headerFields, dynamicTable))
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.CompressionError, ct);
            return true;
        }

        // Check for WebSocket over HTTP/2 (RFC 8441 extended CONNECT)
        string? protocol = null;

        // Extract pseudo-headers and regular headers with RFC 7540 §8.1.2 validations.
        string? method = null;
        string? path = null;
        string? scheme = null;
        string? authority = null;
        var regularHeaders = new List<KeyValuePair<string, string>>();
        var headerDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Validate pseudo-headers and extract values
        var validation = ValidateHeadersPublic(headerFields);
        if (!validation.IsValid)
        {
            await SendRstStreamAsync(connection, frame.StreamId, Http2ErrorCode.ProtocolError, ct);
            return false;
        }

        method = validation.Method;
        path = validation.Path;
        scheme = validation.Scheme;
        authority = validation.Authority;
        protocol = validation.Protocol;
        regularHeaders = validation.RegularHeaders;
        headerDict = validation.HeaderDict;

        // Validate required pseudo-headers — stream-level error, not connection-level
        if (method is null || path is null)
        {
            await SendRstStreamAsync(connection, frame.StreamId, Http2ErrorCode.ProtocolError, ct);
            return false;
        }

        // WebSocket over HTTP/2 (RFC 8441): extended CONNECT with :protocol=websocket
        if (
            method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase)
            && string.Equals(protocol, "websocket", StringComparison.OrdinalIgnoreCase)
        )
        {
            return await ProcessWebSocketOverHttp2(
                connection,
                frame,
                state,
                regularHeaders,
                headerDict,
                ct
            );
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

        // State machine: EndStream received
        state.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.EndStream, out _);

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

        if (response.Body.Length == 0 && response.BodyStream is null)
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

        // BodyStream (streaming) — reuse SendResponseAsync logic
        if (response.BodyStream is not null)
        {
            await SendResponseAsync(
                connection, state, response, frame.StreamId, null, ct);
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

        // Send DATA frame(s) with body, chunked by max frame size
        {
            var bodyBytes = response.Body.ToArray();
            var connState = connection.UserState as ConnectionRuntimeState;
            var maxFrame = connState?.RemoteMaxFrameSize ?? 16384;
            var offset = 0;

            while (offset < bodyBytes.Length)
            {
                var chunkSize = Math.Min(bodyBytes.Length - offset, maxFrame);
                var isLast = offset + chunkSize >= bodyBytes.Length;

                var frameRented = ArrayPool<byte>.Shared.Rent(
                    Http2FrameCodec.FrameHeaderSize + chunkSize
                );
                try
                {
                    var flags = isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
                    Http2FrameCodec.WriteFrame(
                        frameRented,
                        Http2FrameType.Data,
                        flags,
                        frame.StreamId,
                        bodyBytes.AsSpan(offset, chunkSize)
                    );
                    await connection.SendAsync(
                        new ReadOnlySequence<byte>(
                            frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + chunkSize)
                        ),
                        ct
                    );
                    offset += chunkSize;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frameRented);
                }
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

    private static Http2StreamState? GetStreamState(ITcpConnectionContext connection, int streamId)
    {
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
        {
            state = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };
            connection.UserState = state;
        }

        return state.GetOrCreateStream(streamId);
    }

    private static async ValueTask<bool> ProcessWebSocketOverHttp2(
        ITcpConnectionContext connection,
        Http2Frame frame,
        Http2StreamState state,
        List<KeyValuePair<string, string>> regularHeaders,
        Dictionary<string, string> headerDict,
        CancellationToken ct
    )
    {
        // Send 200 response to complete the extended CONNECT handshake
        var responseHeaders = new List<(string, string)> { (":status", "200") };

        var hpackSize = MeasureResponseHeadersSize(responseHeaders);
        var frameRented = ArrayPool<byte>.Shared.Rent(Http2FrameCodec.FrameHeaderSize + hpackSize);
        try
        {
            Http2FrameCodec.WriteFrameHeader(
                frameRented,
                hpackSize,
                Http2FrameType.Headers,
                Http2FrameFlags.EndHeaders,
                state.StreamId
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

        // The tunnel is established. Subsequent DATA frames on this stream
        // carry WebSocket frames. For the MVP, we echo received data on the
        // same stream as an opaque tunnel. Full WebSocket frame encoding
        // would require bridging WebSocketFrameCodec with HTTP/2 DATA frames.
        state.ResponseSent = true;
        return false;
    }

    internal static HeaderValidationResult ValidateHeadersPublic(
        List<(string Name, string Value)> headerFields
    )
    {
        string? method = null,
            path = null,
            scheme = null;
        string? authority = null,
            protocol = null;
        var regularHeaders = new List<KeyValuePair<string, string>>();
        var headerDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool pseudoEnded = false;
        bool hasMethod = false,
            hasPath = false,
            hasScheme = false,
            hasAuthority = false;

        foreach (var (name, value) in headerFields)
        {
            if (!name.StartsWith(':'))
            {
                pseudoEnded = true;
            }
            else
            {
                if (pseudoEnded)
                    return HeaderValidationResult.Invalid();

                switch (name)
                {
                    case ":method":
                        if (hasMethod)
                            return HeaderValidationResult.Invalid();
                        method = value;
                        hasMethod = true;
                        break;
                    case ":path":
                        if (hasPath)
                            return HeaderValidationResult.Invalid();
                        path = value;
                        hasPath = true;
                        break;
                    case ":scheme":
                        if (hasScheme)
                            return HeaderValidationResult.Invalid();
                        scheme = value;
                        hasScheme = true;
                        break;
                    case ":authority":
                        if (hasAuthority)
                            return HeaderValidationResult.Invalid();
                        authority = value;
                        hasAuthority = true;
                        break;
                    case ":protocol":
                        protocol = value;
                        break;
                }
                continue;
            }

            var lower = name.ToLowerInvariant();
            if (
                lower
                is "connection"
                    or "keep-alive"
                    or "proxy-connection"
                    or "transfer-encoding"
                    or "upgrade"
            )
                return HeaderValidationResult.Invalid();

            if (lower == "te" && !value.Equals("trailers", StringComparison.OrdinalIgnoreCase))
                return HeaderValidationResult.Invalid();

            regularHeaders.Add(new(name, value));
            if (!headerDict.ContainsKey(name))
                headerDict[name] = value;
        }

        return new HeaderValidationResult(
            true,
            method,
            path,
            scheme,
            authority,
            protocol,
            regularHeaders,
            headerDict
        );
    }

    internal sealed record HeaderValidationResult(
        bool IsValid,
        string? Method,
        string? Path,
        string? Scheme,
        string? Authority,
        string? Protocol,
        List<KeyValuePair<string, string>>? RegularHeaders,
        Dictionary<string, string>? HeaderDict
    )
    {
        internal static readonly HeaderValidationResult InvalidResult = new(
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null
        );

        internal static HeaderValidationResult Invalid() => InvalidResult;
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

    public static async ValueTask<bool> ProcessWindowUpdateFrame(
        ITcpConnectionContext connection,
        Http2Frame frame,
        CancellationToken ct
    )
    {
        if (frame.Payload.Length < 4)
            return false;

        // Parse window size increment (4 bytes, reserved bit ignored)
        var increment =
            (frame.Payload.Span[0] << 24)
            | (frame.Payload.Span[1] << 16)
            | (frame.Payload.Span[2] << 8)
            | frame.Payload.Span[3];

        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
            return false;

        if (frame.StreamId == 0)
        {
            // Connection-level window update
            state.ConnectionSendWindow += increment;

            // Distribute window across pending streams fairly (round-robin)
            if (state.Http2Streams is not null && state.Http2Streams.Count > 0)
            {
                // Collect streams with pending data, sorted for consistent ordering
                var pending = state
                    .Http2Streams.Where(k =>
                        k.Value.PendingDataFrame is not null && !k.Value.ResponseSent
                    )
                    .Select(k => k.Key)
                    .OrderBy(id => id)
                    .ToList();

                if (pending.Count > 0)
                {
                    // Start from the last served stream for fairness
                    var startIdx = pending.IndexOf(state.LastServedStreamId);
                    if (startIdx < 0)
                        startIdx = 0;

                    for (int i = 0; i < pending.Count; i++)
                    {
                        var idx = (startIdx + i) % pending.Count;
                        var sid = pending[idx];
                        if (state.Http2Streams.TryGetValue(sid, out var stream))
                        {
                            await FlushPendingDataAsync(connection, stream, state, ct);
                        }
                    }

                    state.LastServedStreamId = pending[
                        (startIdx + pending.Count - 1) % pending.Count
                    ];
                }
            }
        }
        else if (state.Http2Streams?.TryGetValue(frame.StreamId, out var stream) == true)
        {
            // Stream-level window update
            stream.SendWindow += increment;
            await FlushPendingDataAsync(connection, stream, state, ct);
        }

        return false;
    }

    private static async ValueTask FlushPendingDataAsync(
        ITcpConnectionContext connection,
        Http2StreamState stream,
        ConnectionRuntimeState state,
        CancellationToken ct
    )
    {
        if (stream.PendingDataFrame is null || stream.ResponseSent)
            return;

        var maxFrame = state.RemoteMaxFrameSize;
        var data = stream.PendingDataFrame;
        var offset = 0;

        while (offset < data.Length)
        {
            var available = Math.Min(state.ConnectionSendWindow, stream.SendWindow);
            if (available <= 0)
            {
                // Update pending to reflect what we've sent so far
                stream.PendingDataFrame = offset > 0 ? data[offset..] : data;
                return;
            }

            var chunkSize = Math.Min(data.Length - offset, maxFrame);
            var toSend = Math.Min(chunkSize, available);
            var isLast = offset + toSend >= data.Length;

            var frameRented = ArrayPool<byte>.Shared.Rent(Http2FrameCodec.FrameHeaderSize + toSend);
            try
            {
                var dataFlags = isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
                Http2FrameCodec.WriteFrame(
                    frameRented,
                    Http2FrameType.Data,
                    dataFlags,
                    stream.StreamId,
                    data.AsSpan(offset, toSend)
                );
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(
                        frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + toSend)
                    ),
                    ct
                );

                state.ConnectionSendWindow -= toSend;
                stream.SendWindow -= toSend;
                offset += toSend;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frameRented);
            }
        }

        stream.PendingDataFrame = null;
        stream.ResponseSent = true;
    }

    public static ValueTask<bool> ProcessRstStreamFrame(
        ITcpConnectionContext connection,
        Http2Frame frame,
        CancellationToken ct
    )
    {
        // Update state machine to Closed, then remove stream.
        var runtimeState = connection.UserState as ConnectionRuntimeState;
        if (runtimeState?.Http2Streams?.TryGetValue(frame.StreamId, out var rstState) == true)
        {
            rstState.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.RstStream, out _);
            runtimeState.Http2Streams.Remove(frame.StreamId);
        }

        return ValueTask.FromResult(false);
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
        if (
            runtimeState?.Http2Streams?.TryGetValue(frame.StreamId, out var state) != true
            || state is null
        )
        {
            return false;
        }

        // Update activity timestamp for timeout tracking
        state.LastActivityUtc = DateTime.UtcNow;

        // State machine: validate DATA is legal in current state
        if (!state.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.Data, out _))
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
            state.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.EndStream, out _);
            return await CompleteDeferredRequest(connection, state, requestHandler, logger, ct);
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
            var bodyStream =
                bodyBytes.Length > 0
                    ? new ReadOnlySequenceStream(new ReadOnlySequence<byte>(bodyBytes))
                    : Stream.Null;

            var request = new HttpRequest
            {
                Method = state.DecodedMethod ?? "GET",
                Target = state.DecodedPath ?? "/",
                Path = state.DecodedPath ?? "/",
                Version = PicoNode.Http.HttpVersion.Http11,
                HeaderFields = state.DecodedHeaderFields ?? [],
                Headers =
                    state.DecodedHeadersDict
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                BodyStream = bodyStream,
            };

            // Invoke handler
            var response = await requestHandler(request, ct);
            state.HandlerInvoked = true;

            // Send response
            await SendResponseAsync(connection, state, response, state.StreamId, logger, ct);
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
                "Unhandled exception processing HTTP/2 deferred stream",
                ex
            );

            var errorResponse = new HttpResponse
            {
                StatusCode = 500,
                ReasonPhrase = "Internal Server Error",
            };
            await SendResponseAsync(connection, state, errorResponse, state.StreamId, logger, ct);
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
            if (
                keyLower
                is "connection"
                    or "transfer-encoding"
                    or "keep-alive"
                    or "proxy-connection"
                    or "upgrade"
            )
                continue;
            responseHeaders.Add((header.Key, header.Value));
        }

        var hpackSize = MeasureResponseHeadersSize(responseHeaders);
        var headersFlags = Http2FrameFlags.EndHeaders;

        if (response.Body.Length == 0 && response.BodyStream is null)
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
                    streamId
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
            return;
        }

        // Has body: HEADERS (no EndStream) + DATA (with EndStream)
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
                    streamId
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

        // Send DATA from BodyStream (streaming response)
        if (response.BodyStream is not null)
        {
            var connState = connection.UserState as ConnectionRuntimeState;
            var maxFrame = connState?.RemoteMaxFrameSize ?? 16384;
            var bufferSize = 4096;
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            try
            {
                var stream = response.BodyStream;
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(0, bufferSize), ct)) > 0)
                {
                    var offset = 0;
                    while (offset < bytesRead)
                    {
                        var connWindow = connState?.ConnectionSendWindow ?? int.MaxValue;
                        var streamWindow = state?.SendWindow ?? int.MaxValue;
                        var available = Math.Min(connWindow, streamWindow);

                        if (available <= 0)
                        {
                            if (state is not null)
                                state.PendingDataFrame = buffer[offset..(offset + bytesRead - offset)];
                            return;
                        }

                        var chunkSize = Math.Min(bytesRead - offset, maxFrame);
                        var sendSize = Math.Min(chunkSize, available);

                        var frameRented = ArrayPool<byte>.Shared.Rent(
                            Http2FrameCodec.FrameHeaderSize + sendSize);
                        try
                        {
                            Http2FrameCodec.WriteFrame(frameRented, Http2FrameType.Data,
                                Http2FrameFlags.None, streamId,
                                buffer.AsSpan(offset, sendSize));
                            await connection.SendAsync(new ReadOnlySequence<byte>(
                                frameRented.AsMemory(0,
                                    Http2FrameCodec.FrameHeaderSize + sendSize)), ct);

                            if (connState is not null)
                                connState.ConnectionSendWindow -= sendSize;
                            if (state is not null)
                                state.SendWindow -= sendSize;
                            offset += sendSize;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(frameRented);
                        }
                    }
                }

                // Stream complete — send final DATA with EndStream
                var finalFrame = ArrayPool<byte>.Shared.Rent(Http2FrameCodec.FrameHeaderSize);
                try
                {
                    Http2FrameCodec.WriteFrameHeader(finalFrame, 0,
                        Http2FrameType.Data, Http2FrameFlags.EndStream, streamId);
                    await connection.SendAsync(new ReadOnlySequence<byte>(
                        finalFrame.AsMemory(0, Http2FrameCodec.FrameHeaderSize)), ct);

                    if (state is not null)
                        state.ResponseSent = true;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(finalFrame);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await SendRstStreamAsync(connection, streamId,
                    Http2ErrorCode.Cancel, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return;
        }

        // Send DATA frames, chunked by max frame size and flow control window
        if (response.Body.Length > 0)
        {
            var data = response.Body.ToArray();
            var connState = connection.UserState as ConnectionRuntimeState;
            var maxFrame = connState?.RemoteMaxFrameSize ?? 16384;

            // Send in chunks
            var offset = 0;
            while (offset < data.Length)
            {
                var chunkSize = Math.Min(data.Length - offset, maxFrame);

                var connWindow = connState?.ConnectionSendWindow ?? int.MaxValue;
                var streamWindow = state?.SendWindow ?? int.MaxValue;
                var available = Math.Min(connWindow, streamWindow);

                if (available <= 0)
                {
                    // Buffer remaining for WINDOW_UPDATE
                    if (state is not null)
                        state.PendingDataFrame = data[offset..];
                    return;
                }

                var sendSize = Math.Min(chunkSize, available);
                var isLast = offset + sendSize >= data.Length;

                var frameRented = ArrayPool<byte>.Shared.Rent(
                    Http2FrameCodec.FrameHeaderSize + sendSize
                );
                try
                {
                    var flags = isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
                    Http2FrameCodec.WriteFrame(
                        frameRented,
                        Http2FrameType.Data,
                        flags,
                        streamId,
                        data.AsSpan(offset, sendSize)
                    );
                    await connection.SendAsync(
                        new ReadOnlySequence<byte>(
                            frameRented.AsMemory(0, Http2FrameCodec.FrameHeaderSize + sendSize)
                        ),
                        ct
                    );

                    if (connState is not null)
                        connState.ConnectionSendWindow -= sendSize;
                    if (state is not null)
                        state.SendWindow -= sendSize;

                    offset += sendSize;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frameRented);
                }
            }

            if (state is not null)
                state.ResponseSent = true;
            return;
        }

        if (state is not null)
            state.ResponseSent = true;
    }

    private static async ValueTask SendRstStreamAsync(
        ITcpConnectionContext connection,
        int streamId,
        Http2ErrorCode errorCode,
        CancellationToken ct
    )
    {
        // RST_STREAM has a 4-byte payload for the error code
        var payload = new byte[4];
        payload[0] = (byte)(((int)errorCode >> 24) & 0xFF);
        payload[1] = (byte)(((int)errorCode >> 16) & 0xFF);
        payload[2] = (byte)(((int)errorCode >> 8) & 0xFF);
        payload[3] = (byte)((int)errorCode & 0xFF);

        var frame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.RstStream,
            Http2FrameFlags.None,
            streamId,
            payload
        );
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct);

        // Remove the stream from tracking
        var state = connection.UserState as ConnectionRuntimeState;
        state?.Http2Streams?.Remove(streamId);
    }

    private static async ValueTask SendGoAwayAndCloseAsync(
        ITcpConnectionContext connection,
        Http2ErrorCode errorCode,
        CancellationToken ct
    )
    {
        // Use the highest processed stream ID for graceful shutdown signalling.
        var lastStreamId = 0;
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is not null)
            lastStreamId = state.HighestProcessedStreamId;

        var frame = Http2FrameCodec.EncodeGoAway(lastStreamId, errorCode);
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct);
        connection.Close();
    }
}
