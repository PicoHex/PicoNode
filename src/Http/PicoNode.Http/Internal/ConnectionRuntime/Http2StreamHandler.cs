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
            // Our own advertised limit governs how many streams the PEER may open
            // (the peer's SETTINGS value only limits what WE open).
            if (streamCount >= runtimeStateForLimit.LocalMaxConcurrentStreams)
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
            // Invalid stream ID (even, non-monotonic, or stream 0) — a
            // connection error, not a stream error (RFC 7540 §5.1.1).
            var unknownRuntime = connection.UserState as ConnectionRuntimeState;
            if (unknownRuntime?.GoAwayReceived == true)
            {
                return false;
            }

            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
            return true;
        }

        // Update activity timestamp for timeout tracking
        state.LastActivityUtc = DateTime.UtcNow;

        // State machine: validate HEADERS is legal in current state
        if (
            !state.StateMachine.TryTransition(
                Http2StreamStateMachine.Trigger.Headers,
                out var prevState
            )
        )
        {
            if (prevState == Http2StreamStateMachine.StreamState.HalfClosedRemote)
            {
                // §5.1: additional frames after the peer's END_STREAM →
                // stream error STREAM_CLOSED.
                await SendRstStreamAsync(
                        connection,
                        frame.StreamId,
                        Http2ErrorCode.StreamClosed,
                        ct
                    )
                    .ConfigureAwait(false);
                return false;
            }

            if (prevState == Http2StreamStateMachine.StreamState.Closed)
            {
                if (state.StateMachine.ClosedByPeerRst)
                {
                    // §5.1: frames after the peer's RST_STREAM → stream error
                    // STREAM_CLOSED.
                    await SendRstStreamAsync(
                            connection,
                            frame.StreamId,
                            Http2ErrorCode.StreamClosed,
                            ct
                        )
                        .ConfigureAwait(false);
                    return false;
                }

                // §5.1: frames on a closed stream (closed via END_STREAM) →
                // connection error STREAM_CLOSED.
                await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.StreamClosed, ct)
                    .ConfigureAwait(false);
                return true;
            }

            logger?.Log(
                LogLevel.Debug,
                $"[H2] State transition failed: StreamId={frame.StreamId} State={prevState} Trigger=Headers, sending RST_STREAM PROTOCOL_ERROR",
                null
            );
            await SendRstStreamAsync(connection, frame.StreamId, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
            return false;
        }

        // CONTINUATION buffering: if END_HEADERS is not set, buffer and return.
        ArraySegment<byte>? payloadData;
        try
        {
            var headerPayload = frame.Payload;

            // RFC 7540 §6.2: HEADERS with PADDED flag has a 1-byte Pad Length
            // field at the start, followed by that many padding octets.
            if (frame.HasFlag(Http2FrameFlags.Padded))
            {
                if (headerPayload.Length < 1)
                {
                    await SendRstStreamAsync(
                            connection,
                            frame.StreamId,
                            Http2ErrorCode.ProtocolError,
                            ct
                        )
                        .ConfigureAwait(false);
                    return false;
                }

                var padLength = headerPayload.Span[0];
                headerPayload = headerPayload.Slice(1);
                if (padLength > headerPayload.Length)
                {
                    await SendRstStreamAsync(
                            connection,
                            frame.StreamId,
                            Http2ErrorCode.ProtocolError,
                            ct
                        )
                        .ConfigureAwait(false);
                    return false;
                }

                headerPayload = headerPayload.Slice(0, headerPayload.Length - padLength);
            }

            // RFC 7540 §6.2: HEADERS with PRIORITY flag has 5 extra bytes
            // at the start: Exclusive(1b) + StreamDependency(31b) + Weight(8b).
            // Slice them off before passing to HPACK decompression.
            if (frame.HasFlag(Http2FrameFlags.Priority))
            {
                if (headerPayload.Length < 5)
                {
                    await SendRstStreamAsync(
                        connection,
                        frame.StreamId,
                        Http2ErrorCode.FrameSizeError,
                        ct
                    );
                    return false;
                }

                // RFC 7540 §5.3.1: a stream cannot depend on itself.
                if (
                    Http2FrameCodec.TryGetStreamDependency(headerPayload.Span, out var dependency)
                    && dependency == frame.StreamId
                )
                {
                    await SendRstStreamAsync(
                            connection,
                            frame.StreamId,
                            Http2ErrorCode.ProtocolError,
                            ct
                        )
                        .ConfigureAwait(false);
                    return false;
                }

                headerPayload = headerPayload.Slice(5);
            }

            payloadData = state.AppendHeaderData(
                headerPayload,
                frame.HasFlag(Http2FrameFlags.EndHeaders)
            );
        }
        catch (Http2HeaderTooLargeException)
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.CompressionError, ct)
                .ConfigureAwait(false);
            return true;
        }

        if (payloadData is null)
        {
            // Headers not yet complete (waiting for CONTINUATION).
            // Save the EndStream flag from the HEADERS frame for later use.
            // CONTINUATION frames never carry the EndStream flag — only the
            // original HEADERS frame decides it.
            if (frame.Type == Http2FrameType.Headers)
            {
                state.EndStreamFromHeaders = frame.HasFlag(Http2FrameFlags.EndStream);
            }

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
        if (
            !HpackDecoder.TryDecode(
                payloadData.Value.AsSpan(),
                out var headerFields,
                dynamicTable,
                ConnectionRuntimeState.LocalHeaderTableSize
            )
        )
        {
            logger?.Log(
                LogLevel.Debug,
                $"[H2] HPACK decode failed: StreamId={frame.StreamId} payloadLen={payloadData.Value.Count}, sending GOAWAY COMPRESSION_ERROR",
                null
            );
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.CompressionError, ct)
                .ConfigureAwait(false);
            return true;
        }

        // Extract pseudo-headers and regular headers with RFC 7540 §8.1.2 validations.
        string? method = null;
        string? path = null;
        string? scheme = null;
        string? authority = null;
        string? protocol = null;
        List<KeyValuePair<string, string>> regularHeaders;
        Dictionary<string, string> headerDict;

        // Trailers (§8.1): a HEADERS frame arriving on a stream whose
        // request headers are already complete. Trailers MUST NOT contain
        // pseudo-header fields and MUST carry END_STREAM. Valid trailers
        // complete the request — fall through to the common handler
        // invocation path below.
        if (frame.Type == Http2FrameType.Headers && state.DecodedMethod is not null)
        {
            if (
                headerFields.Any(h => h.Item1.StartsWith(':'))
                || !frame.HasFlag(Http2FrameFlags.EndStream)
            )
            {
                await SendRstStreamAsync(
                        connection,
                        frame.StreamId,
                        Http2ErrorCode.ProtocolError,
                        ct
                    )
                    .ConfigureAwait(false);
                return false;
            }

            foreach (var (name, value) in headerFields)
            {
                state.DecodedHeadersDict![name] = value;
            }

            method = state.DecodedMethod;
            path = state.DecodedPath;
            scheme = state.DecodedScheme;
            regularHeaders = state.DecodedHeaderFields ?? [];
            headerDict = state.DecodedHeadersDict!;
            state.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.EndStream, out _);
        }
        else
        {
            var validation = ValidateHeadersPublic(headerFields);
            if (!validation.IsValid)
            {
                await SendRstStreamAsync(
                        connection,
                        frame.StreamId,
                        Http2ErrorCode.ProtocolError,
                        ct
                    )
                    .ConfigureAwait(false);
                return false;
            }

            method = validation.Method;
            path = validation.Path;
            scheme = validation.Scheme;
            authority = validation.Authority;
            protocol = validation.Protocol;
            regularHeaders = validation.RegularHeaders!;
            headerDict = validation.HeaderDict!;
        }

        // Validate required pseudo-headers — stream-level error, not connection-level
        if (method is null || path is null)
        {
            await SendRstStreamAsync(connection, frame.StreamId, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
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
            StoreDecodedHeaders(state, method, path, scheme, regularHeaders, headerDict);
            state.EndStreamReceived = false;
            return false;
        }

        // State machine: EndStream received
        state.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.EndStream, out _);

        // RFC 7540 §8.1.2.6: content-length must match the request body.
        if (!await ValidateContentLengthAsync(connection, state, 0, ct))
        {
            return false;
        }

        // Construct HttpRequest
        var request = new HttpRequest
        {
            Method = method,
            Target = path,
            Path = path,
            Version = HttpVersion.Http2,
            HeaderFields = regularHeaders,
            Headers = headerDict,
        };

        // Invoke request handler
        HttpResponse response;
        try
        {
            response = await requestHandler(request, ct).ConfigureAwait(false);
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

        // Encode response headers as HPACK block in a temporary buffer,
        // then write HEADERS frame using a single pooled buffer
        var headerWriter = new ArrayBufferWriter<byte>();
        EncodeResponseHeadersHpack(connection, responseHeaders, headerWriter);
        var headersFlags = Http2FrameFlags.EndHeaders;

        if (response.Body.Length == 0 && response.BodyStream is null)
        {
            headersFlags |= Http2FrameFlags.EndStream;
            await WriteHeadersFrameAsync(
                connection,
                frame.StreamId,
                headersFlags,
                headerWriter.WrittenMemory,
                ct
            );
            state.CompleteResponse();
            return false;
        }

        // Has body: HEADERS (no EndStream) + DATA (with EndStream) sent by a
        // background pump (see SendResponseAsync). Buffered bodies go through
        // the same path as streaming bodies so flow-control windows are
        // respected uniformly.
        await SendResponseAsync(connection, state, response, frame.StreamId, null, ct);
        return false;
    }

    // ── HPACK response encoder (uses the connection's shared HpackEncoder, whose
    //    dynamic table is resized by peer SETTINGS — see ConnectionRuntimeState) ──

    private static void EncodeResponseHeadersHpack(
        ITcpConnectionContext connection,
        List<(string Name, string Value)> headers,
        IBufferWriter<byte> writer
    )
    {
        // One encoder per connection: HPACK dynamic tables are per-connection by
        // RFC 7541 §2.3. The encoder table capacity tracks the peer's advertised
        // SETTINGS_HEADER_TABLE_SIZE so indices never desync from the peer decoder.
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
        {
            state = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };
            connection.UserState = state;
        }
        state.ResponseHpackEncoder.Encode(writer, headers);
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

        var headerWriter = new ArrayBufferWriter<byte>();
        EncodeResponseHeadersHpack(connection, responseHeaders, headerWriter);
        await WriteHeadersFrameAsync(
            connection,
            state.StreamId,
            Http2FrameFlags.EndHeaders,
            headerWriter.WrittenMemory,
            ct
        );

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

                // RFC 7540 §8.1.2: header field names MUST be lowercase.
                if (!name.Equals(name.ToLowerInvariant(), StringComparison.Ordinal))
                    return HeaderValidationResult.Invalid();
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
                        if (value.Length == 0)
                            return HeaderValidationResult.Invalid();
                        if (!IsValidPathPercentEncoding(value))
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
                    default:
                        // RFC 7540 §8.1.2.1: unknown pseudo-header fields (and
                        // response pseudo-headers like :status in a request)
                        // MUST be treated as malformed.
                        return HeaderValidationResult.Invalid();
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

        // RFC 7540 §8.1.2.3: :method, :path and :scheme are REQUIRED in
        // requests. (Pseudo-header presence/absence for trailers is checked
        // separately by the caller.)
        if (!hasMethod || !hasPath || !hasScheme)
            return HeaderValidationResult.Invalid();

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

    /// <summary>
    /// Validates %-escapes in an HTTP/2 :path the same way the HTTP/1.1
    /// request-line parser does: '%' must be followed by exactly two hex
    /// digits. Malformed escapes would otherwise surface as
    /// Uri.UnescapeDataString exceptions (500) during routing.
    /// </summary>
    private static bool IsValidPathPercentEncoding(string path)
    {
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] != '%')
                continue;

            if (
                i + 2 >= path.Length
                || !HttpParseHelpers.IsHexDigit((byte)path[i + 1])
                || !HttpParseHelpers.IsHexDigit((byte)path[i + 2])
            )
            {
                return false;
            }

            i += 2;
        }

        return true;
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
        string? scheme,
        List<KeyValuePair<string, string>> regularHeaders,
        Dictionary<string, string> headerDict
    )
    {
        state.DecodedMethod = method;
        state.DecodedPath = path;
        state.DecodedScheme = scheme;
        state.DecodedHeaderFields = regularHeaders;
        state.DecodedHeadersDict = headerDict;
    }

    public static async ValueTask<bool> ProcessWindowUpdateFrame(
        ITcpConnectionContext connection,
        Http2Frame frame,
        CancellationToken ct
    )
    {
        // RFC 7540 §6.9: a WINDOW_UPDATE frame with a length other than 4 octets
        // MUST be treated as a connection error of type FRAME_SIZE_ERROR.
        if (frame.Payload.Length != 4)
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.FrameSizeError, ct)
                .ConfigureAwait(false);
            return true;
        }

        // Parse window size increment (4 bytes, reserved bit ignored)
        var increment =
            (frame.Payload.Span[0] << 24)
            | (frame.Payload.Span[1] << 16)
            | (frame.Payload.Span[2] << 8)
            | frame.Payload.Span[3];

        // RFC 7540 §6.9: a WINDOW_UPDATE with increment 0 is a protocol error
        // (stream-level → stream error; connection-level → connection error).
        if (increment == 0)
        {
            if (frame.StreamId == 0)
            {
                await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                    .ConfigureAwait(false);
                return true;
            }

            await SendRstStreamAsync(connection, frame.StreamId, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
            return false;
        }

        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
            return false;

        if (frame.StreamId == 0)
        {
            // RFC 7540 §6.9: an increment that makes the window exceed 2^31-1
            // MUST be treated as FLOW_CONTROL_ERROR. Unchecked accumulation
            // would overflow the int and stall every response send forever.
            if (state.ConnectionSendWindow > int.MaxValue - increment)
            {
                await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.FlowControlError, ct)
                    .ConfigureAwait(false);
                return true;
            }

            // Connection-level window update
            state.AddConnectionSendWindow(increment);

            // Wake every response pump blocked on flow-control backpressure.
            // Each pump re-checks the windows and goes back to sleep if still
            // insufficient.
            state.ReleaseAllFlowControlSignals();
        }
        else if (state.Http2Streams?.TryGetValue(frame.StreamId, out var stream) == true)
        {
            // Same 2^31-1 bound at stream level (RFC 7540 §6.9.1).
            if (stream.SendWindow > int.MaxValue - increment)
            {
                await SendRstStreamAsync(
                        connection,
                        frame.StreamId,
                        Http2ErrorCode.FlowControlError,
                        ct
                    )
                    .ConfigureAwait(false);
                return false;
            }

            // Stream-level window update
            stream.AddSendWindow(increment);
            stream.FlowControlSignal?.Release();
        }
        else
        {
            // RFC 7540 §6.9: WINDOW_UPDATE for an idle stream is a connection
            // error of type PROTOCOL_ERROR.
            if (state.GoAwayReceived)
            {
                return false;
            }

            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
            return true;
        }

        return false;
    }

    public static async ValueTask<bool> ProcessRstStreamFrame(
        ITcpConnectionContext connection,
        Http2Frame frame,
        CancellationToken ct
    )
    {
        // RFC 7540 §6.4: a RST_STREAM frame with a length other than 4 octets
        // MUST be treated as a connection error of type FRAME_SIZE_ERROR.
        if (frame.Payload.Length != 4)
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.FrameSizeError, ct)
                .ConfigureAwait(false);
            return true;
        }

        // RFC 7540 §6.4: RST_STREAM with stream identifier 0x0 is a
        // connection error of type PROTOCOL_ERROR.
        if (frame.StreamId == 0)
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
            return true;
        }

        // Update state machine to Closed. The stream state is kept as a
        // tombstone so later frames on the closed stream get a STREAM_CLOSED
        // error instead of being treated as idle.
        var runtimeState = connection.UserState as ConnectionRuntimeState;
        if (runtimeState?.Http2Streams?.TryGetValue(frame.StreamId, out var rstState) != true)
        {
            // RFC 7540 §6.4: RST_STREAM for an idle stream is a connection
            // error of type PROTOCOL_ERROR.
            if (runtimeState?.GoAwayReceived == true)
            {
                return false;
            }

            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
            return true;
        }

        rstState!.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.RstStream, out _);
        // Stop the response pump if one is streaming — without this, a
        // stalled body producer would keep the pump (and its task) alive
        // for the lifetime of the connection.
        rstState.ResponseCts?.Cancel();

        return false;
    }

    public static async ValueTask<bool> ProcessDataFrame(
        ITcpConnectionContext connection,
        Http2Frame frame,
        HttpRequestHandler requestHandler,
        ILogger? logger,
        CancellationToken ct
    )
    {
        // RFC 7540 §6.1: DATA on stream 0 is a connection error.
        if (frame.StreamId == 0)
        {
            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
            return true;
        }

        // Validate stream exists. A DATA frame for a stream that was never
        // opened (idle) is a connection error of type PROTOCOL_ERROR
        // (RFC 7540 §5.1).
        var runtimeState = connection.UserState as ConnectionRuntimeState;
        if (
            runtimeState?.Http2Streams?.TryGetValue(frame.StreamId, out var state) != true
            || state is null
        )
        {
            if (runtimeState?.GoAwayReceived == true)
            {
                return false;
            }

            await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                .ConfigureAwait(false);
            return true;
        }

        // Update activity timestamp for timeout tracking
        state.LastActivityUtc = DateTime.UtcNow;

        // State machine: validate DATA is legal in current state. Frames on
        // half-closed (remote) / closed streams → stream error STREAM_CLOSED.
        if (!state.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.Data, out _))
        {
            await SendRstStreamAsync(connection, frame.StreamId, Http2ErrorCode.StreamClosed, ct)
                .ConfigureAwait(false);
            return false;
        }

        // RFC 7540 §6.1: DATA with PADDED flag has a 1-byte Pad Length field
        // at the start. The padding octets are not part of the body.
        var dataPayload = frame.Payload;
        if (frame.HasFlag(Http2FrameFlags.Padded))
        {
            if (dataPayload.Length < 1)
            {
                await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                    .ConfigureAwait(false);
                return true;
            }

            var padLength = dataPayload.Span[0];
            dataPayload = dataPayload.Slice(1);
            if (padLength > dataPayload.Length)
            {
                // RFC 7540 §6.1: pad length >= payload length is a CONNECTION
                // error of type PROTOCOL_ERROR.
                await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, ct)
                    .ConfigureAwait(false);
                return true;
            }

            dataPayload = dataPayload.Slice(0, dataPayload.Length - padLength);
        }

        // Buffer the data
        if (dataPayload.Length > 0)
        {
            // RFC 7540 §6.9: an endpoint MUST treat a DATA frame exceeding a receive
            // window as a flow-control error.
            if (dataPayload.Length > state.ReceiveWindow)
            {
                await SendRstStreamAsync(
                        connection,
                        frame.StreamId,
                        Http2ErrorCode.FlowControlError,
                        ct
                    )
                    .ConfigureAwait(false);
                return false;
            }

            if (
                runtimeState is not null
                && dataPayload.Length > runtimeState.ConnectionReceiveWindow
            )
            {
                // Connection-level window exceeded — connection error per RFC 7540 §6.9.1.
                await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.FlowControlError, ct)
                    .ConfigureAwait(false);
                return true;
            }

            // Check request body size limit (protects against OOM from large or multi-stream bodies)
            var maxBody = runtimeState?.MaxRequestBodyBytes ?? 64 * 1024 * 1024;
            if (state.DataBuffer.WrittenCount > maxBody - dataPayload.Length)
            {
                await SendRstStreamAsync(
                        connection,
                        frame.StreamId,
                        Http2ErrorCode.EnhanceYourCalm,
                        ct
                    )
                    .ConfigureAwait(false);
                return false;
            }
            state.DataBuffer.Write(dataPayload.Span);

            // Flow control: decrement receive windows and send WINDOW_UPDATE
            // when they drop below half the initial window size.
            const int initialWindow = 65535;
            const int windowThreshold = initialWindow / 2;

            state.ReceiveWindow -= dataPayload.Length;

            if (runtimeState is not null)
            {
                runtimeState.AddConnectionReceiveWindow(-dataPayload.Length);

                if (runtimeState.ConnectionReceiveWindow <= windowThreshold)
                {
                    var connIncrement = initialWindow - runtimeState.ConnectionReceiveWindow;
                    runtimeState.ConnectionReceiveWindow = initialWindow;
                    await WriteWindowUpdateFrameAsync(connection, 0, connIncrement, ct)
                        .ConfigureAwait(false);
                }
            }

            if (state.ReceiveWindow <= windowThreshold)
            {
                var streamIncrement = initialWindow - state.ReceiveWindow;
                state.ReceiveWindow = initialWindow;
                await WriteWindowUpdateFrameAsync(connection, frame.StreamId, streamIncrement, ct)
                    .ConfigureAwait(false);
            }
        }

        // If END_STREAM, complete the request and invoke the handler
        if (frame.HasFlag(Http2FrameFlags.EndStream))
        {
            state.EndStreamReceived = true;
            state.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.EndStream, out _);
            return await CompleteDeferredRequest(connection, state, requestHandler, logger, ct)
                .ConfigureAwait(false);
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
                Version = PicoNode.Http.HttpVersion.Http2,
                HeaderFields = state.DecodedHeaderFields ?? [],
                Headers =
                    state.DecodedHeadersDict
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                BodyStream = bodyStream,
            };

            // RFC 7540 §8.1.2.6: content-length must match the request body.
            if (
                !await ValidateContentLengthAsync(
                    connection,
                    state,
                    state.DataBuffer.WrittenCount,
                    ct
                )
            )
            {
                return false;
            }

            // Invoke handler
            var response = await requestHandler(request, ct).ConfigureAwait(false);

            // Send response
            await SendResponseAsync(connection, state, response, state.StreamId, logger, ct)
                .ConfigureAwait(false);
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
            await SendResponseAsync(connection, state, errorResponse, state.StreamId, logger, ct)
                .ConfigureAwait(false);
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

        var headerWriter = new ArrayBufferWriter<byte>();
        EncodeResponseHeadersHpack(connection, responseHeaders, headerWriter);
        var headersFlags = Http2FrameFlags.EndHeaders;
        var encodedHeaders = headerWriter.WrittenMemory;

        if (response.Body.Length == 0 && response.BodyStream is null)
        {
            headersFlags |= Http2FrameFlags.EndStream;
            await WriteHeadersFrameAsync(connection, streamId, headersFlags, encodedHeaders, ct);
            state?.CompleteResponse();
            return;
        }

        // Has body: HEADERS (no EndStream) + DATA (with EndStream) sent by a
        // background pump. The pump is decoupled from the frame loop so a slow
        // or stalled producer (e.g. an SSE stream whose handler never ends)
        // cannot wedge every other request on the connection.
        await WriteHeadersFrameAsync(connection, streamId, headersFlags, encodedHeaders, ct);

        if (state is null)
        {
            // Defensive fallback (unreachable in practice: every caller passes
            // a stream state) — drain inline without flow-control accounting.
            var fallbackStream =
                response.BodyStream
                ?? new ReadOnlySequenceStream(new ReadOnlySequence<byte>(response.Body));
            await using (fallbackStream.ConfigureAwait(false))
            {
                var fallbackBuffer = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    int bytesRead;
                    while (
                        (
                            bytesRead = await fallbackStream
                                .ReadAsync(fallbackBuffer.AsMemory(0, 4096), ct)
                                .ConfigureAwait(false)
                        ) > 0
                    )
                    {
                        await WriteDataFrameAsync(
                                connection,
                                streamId,
                                Http2FrameFlags.None,
                                fallbackBuffer.AsMemory(0, bytesRead),
                                ct
                            )
                            .ConfigureAwait(false);
                    }

                    await WriteDataFrameAsync(
                            connection,
                            streamId,
                            Http2FrameFlags.EndStream,
                            ReadOnlyMemory<byte>.Empty,
                            ct
                        )
                        .ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(fallbackBuffer);
                }
            }

            return;
        }

        var bodyStream =
            response.BodyStream
            ?? new ReadOnlySequenceStream(new ReadOnlySequence<byte>(response.Body));
        state.ResponseCts ??= new CancellationTokenSource();
        state.FlowControlSignal ??= new SemaphoreSlim(0, int.MaxValue);
        state.ResponseBodyStream = bodyStream;
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            state.ResponseCts.Token
        );
        state.ResponsePumpTask = PumpResponseAsync(
            connection,
            connection.UserState as ConnectionRuntimeState,
            state,
            bodyStream,
            linkedCts
        );
    }

    private static async Task PumpResponseAsync(
        ITcpConnectionContext connection,
        ConnectionRuntimeState? connState,
        Http2StreamState stream,
        Stream bodyStream,
        CancellationTokenSource linkedCts
    )
    {
        var ct = linkedCts.Token;
        var maxFrame = connState?.RemoteMaxFrameSize ?? 16384;
        var bufferSize = 4096;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                var bytesRead = await bodyStream
                    .ReadAsync(buffer.AsMemory(0, bufferSize), ct)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                var offset = 0;
                while (offset < bytesRead)
                {
                    var chunkSize = Math.Min(bytesRead - offset, maxFrame);
                    int sendSize;
                    if (connState is not null)
                    {
                        // Atomic check-and-reserve: concurrent pumps share the
                        // connection send window, so read + subtract must not
                        // interleave (would overdraw the window).
                        lock (connState.SendWindowLock)
                        {
                            var available = Math.Min(
                                connState.ConnectionSendWindow,
                                stream.SendWindow
                            );
                            sendSize = Math.Min(chunkSize, available);
                            if (sendSize > 0)
                            {
                                connState.AddConnectionSendWindow(-sendSize);
                                stream.AddSendWindow(-sendSize);
                            }
                        }
                    }
                    else
                    {
                        sendSize = chunkSize;
                    }

                    if (sendSize <= 0)
                    {
                        // Flow-control backpressure: wait until WINDOW_UPDATE /
                        // SETTINGS releases the signal.
                        var signal = stream.FlowControlSignal;
                        if (signal is not null)
                        {
                            await signal.WaitAsync(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(50, ct).ConfigureAwait(false);
                        }

                        continue;
                    }

                    await WriteDataFrameAsync(
                            connection,
                            stream.StreamId,
                            Http2FrameFlags.None,
                            buffer.AsMemory(offset, sendSize),
                            ct
                        )
                        .ConfigureAwait(false);
                    offset += sendSize;
                }
            }

            // Stream complete — send the final DATA with EndStream.
            await WriteDataFrameAsync(
                    connection,
                    stream.StreamId,
                    Http2FrameFlags.EndStream,
                    ReadOnlyMemory<byte>.Empty,
                    ct
                )
                .ConfigureAwait(false);
            stream.CompleteResponse();
        }
        catch (OperationCanceledException)
        {
            // RST_STREAM or connection close — exit quietly. The client
            // aborted the stream; the EndStream frame must not be sent.
        }
        catch (Exception)
        {
            // Producer error (e.g. the SSE pipe completed with an exception).
            try
            {
                await SendRstStreamAsync(
                        connection,
                        stream.StreamId,
                        Http2ErrorCode.Cancel,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            catch
            {
                // Connection is going away — nothing to do.
            }
        }
        finally
        {
            stream.ResponseBodyStream = null;
            stream.ResponsePumpTask = null;
            ArrayPool<byte>.Shared.Return(buffer);
            try
            {
                await bodyStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Disposal is best-effort — the pump task must never fault
                // unobserved.
            }

            linkedCts.Dispose();
        }
    }

    /// <summary>
    /// RFC 7540 §8.1.2.6: a request whose content-length does not equal the
    /// actual request body size is malformed → stream error PROTOCOL_ERROR.
    /// </summary>
    private static async ValueTask<bool> ValidateContentLengthAsync(
        ITcpConnectionContext connection,
        Http2StreamState state,
        int bodyLength,
        CancellationToken ct
    )
    {
        if (
            state.DecodedHeadersDict is { } headers
            && headers.TryGetValue("content-length", out var raw)
        )
        {
            if (!long.TryParse(raw, out var expected) || expected != bodyLength)
            {
                await SendRstStreamAsync(
                        connection,
                        state.StreamId,
                        Http2ErrorCode.ProtocolError,
                        ct
                    )
                    .ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    internal static async ValueTask SendRstStreamAsync(
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
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct).ConfigureAwait(false);

        // Remove the stream from tracking
        var state = connection.UserState as ConnectionRuntimeState;
        state?.Http2Streams?.TryRemove(streamId, out _);
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
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct).ConfigureAwait(false);
        connection.Close();
    }

    // ── Shared frame-writing helpers ─────────────────────────────────────

    /// <summary>Writes a HEADERS frame with HPACK-encoded headers using a pooled buffer.
    /// When the encoded block exceeds the peer's max frame size, it is split across
    /// CONTINUATION frames (RFC 7540 §6.2/§6.10).</summary>
    private static async ValueTask WriteHeadersFrameAsync(
        ITcpConnectionContext connection,
        int streamId,
        Http2FrameFlags flags,
        ReadOnlyMemory<byte> encodedHeaders,
        CancellationToken ct
    )
    {
        var state = connection.UserState as ConnectionRuntimeState;
        var maxFrameSize = Math.Max(
            state?.RemoteMaxFrameSize ?? Http2FrameCodec.DefaultMaxFrameSize,
            Http2FrameCodec.DefaultMaxFrameSize
        );

        if (encodedHeaders.Length <= maxFrameSize)
        {
            var totalSize = Http2FrameCodec.FrameHeaderSize + encodedHeaders.Length;
            var rented = ArrayPool<byte>.Shared.Rent(totalSize);
            try
            {
                Http2FrameCodec.WriteFrameHeader(
                    rented,
                    encodedHeaders.Length,
                    Http2FrameType.Headers,
                    flags,
                    streamId
                );
                encodedHeaders.Span.CopyTo(rented.AsSpan(Http2FrameCodec.FrameHeaderSize));
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(rented.AsMemory(0, totalSize)),
                    ct
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
            return;
        }

        // Split: HEADERS without END_HEADERS, then CONTINUATION frames.
        // END_HEADERS goes on the last frame; other flags (e.g. END_STREAM)
        // stay on the HEADERS frame only.
        var offset = 0;
        var isFirst = true;
        while (offset < encodedHeaders.Length)
        {
            var chunkLength = Math.Min(encodedHeaders.Length - offset, maxFrameSize);
            var isLast = offset + chunkLength >= encodedHeaders.Length;
            var frameType = isFirst ? Http2FrameType.Headers : Http2FrameType.Continuation;
            var frameFlags =
                frameType == Http2FrameType.Headers
                    ? flags & ~Http2FrameFlags.EndHeaders
                    : Http2FrameFlags.None;
            if (isLast)
                frameFlags |= Http2FrameFlags.EndHeaders;

            var frameSize = Http2FrameCodec.FrameHeaderSize + chunkLength;
            var rented = ArrayPool<byte>.Shared.Rent(frameSize);
            try
            {
                Http2FrameCodec.WriteFrameHeader(
                    rented,
                    chunkLength,
                    frameType,
                    frameFlags,
                    streamId
                );
                encodedHeaders
                    .Span.Slice(offset, chunkLength)
                    .CopyTo(rented.AsSpan(Http2FrameCodec.FrameHeaderSize));
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(rented.AsMemory(0, frameSize)),
                    ct
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            offset += chunkLength;
            isFirst = false;
        }
    }

    /// <summary>Writes a DATA frame with the given payload using a pooled buffer.</summary>
    private static async ValueTask WriteDataFrameAsync(
        ITcpConnectionContext connection,
        int streamId,
        Http2FrameFlags flags,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct
    )
    {
        var totalSize = Http2FrameCodec.FrameHeaderSize + payload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            Http2FrameCodec.WriteFrame(rented, Http2FrameType.Data, flags, streamId, payload.Span);
            await connection.SendAsync(
                new ReadOnlySequence<byte>(rented.AsMemory(0, totalSize)),
                ct
            );
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async ValueTask WriteWindowUpdateFrameAsync(
        ITcpConnectionContext connection,
        int streamId,
        int increment,
        CancellationToken ct
    )
    {
        var totalSize = Http2FrameCodec.FrameHeaderSize + 4;
        var rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            Span<byte> payload = stackalloc byte[4];
            payload[0] = (byte)((increment >> 24) & 0x7F);
            payload[1] = (byte)((increment >> 16) & 0xFF);
            payload[2] = (byte)((increment >> 8) & 0xFF);
            payload[3] = (byte)(increment & 0xFF);

            var dest = rented.AsSpan(0, totalSize);
            Http2FrameCodec.WriteFrame(
                dest,
                Http2FrameType.WindowUpdate,
                Http2FrameFlags.None,
                streamId,
                payload
            );
            await connection.SendAsync(
                new ReadOnlySequence<byte>(rented.AsMemory(0, totalSize)),
                ct
            );
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
