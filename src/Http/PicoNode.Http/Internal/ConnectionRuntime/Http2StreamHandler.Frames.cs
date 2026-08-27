namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static partial class Http2StreamHandler
{
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

            request.RemoteCloseToken = connection.RemoteCloseToken;

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
}
