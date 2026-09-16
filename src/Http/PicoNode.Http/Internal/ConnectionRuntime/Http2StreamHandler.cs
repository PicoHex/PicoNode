namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static partial class Http2StreamHandler
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

            // Trailers complete a request that may have buffered DATA frames:
            // reuse the deferred path so the body is delivered to the handler
            // and content-length is validated against the ACTUAL body size
            // (validating against zero rejected every trailered body request).
            return await CompleteDeferredRequest(connection, state, requestHandler, logger, ct);
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

        // Record the decoded request on the stream state so downstream paths
        // (HEAD response handling, content-length validation) see the same
        // request metadata the deferred path stores. Previously the immediate
        // END_STREAM path left DecodedHeadersDict null, silently skipping the
        // RFC 7540 §8.1.2.6 content-length check.
        StoreDecodedHeaders(state, method, path, scheme, regularHeaders, headerDict);

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

        request.RemoteCloseToken = connection.RemoteCloseToken;

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

        // Response headers are HPACK-encoded exactly once inside SendResponseAsync.
        // Encoding here as well would mutate the connection's encoder dynamic table
        // for a header block that is never sent, desynchronising it from the peer's
        // decoder (verified regression: body responses carrying a header outside
        // the HPACK static table became undecodable, e.g. X-Content-Type-Options).
        await SendResponseAsync(connection, state, response, frame.StreamId, logger, ct);
        return false;
    }

    // ── HPACK response encoder (uses the connection's shared HpackEncoder, whose
    //    dynamic table is resized by peer SETTINGS — see ConnectionRuntimeState) ──

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
}
