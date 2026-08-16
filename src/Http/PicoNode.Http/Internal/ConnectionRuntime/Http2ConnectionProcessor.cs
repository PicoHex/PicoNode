namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static class Http2ConnectionProcessor
{
    public static async ValueTask<SequencePosition> ProcessAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        bool sendInitialSettings,
        HttpRequestHandler requestHandler,
        ILogger? logger,
        CancellationToken cancellationToken
    )
    {
        var remaining = buffer;
        var consumed = buffer.Start;

        if (sendInitialSettings)
        {
            var settings =
                (ReadOnlySpan<Http2Setting>)
                    [
                        new(Http2SettingId.MaxConcurrentStreams, 100),
                        new(Http2SettingId.InitialWindowSize, 65535),
                        new(Http2SettingId.HeaderTableSize, 4096),
                    ];
            var size = Http2FrameCodec.FrameHeaderSize + settings.Length * 6;
            var rented = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                Http2FrameCodec.WriteSettings(rented, settings);
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(rented.AsMemory(0, size)),
                    cancellationToken
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        while (remaining.Length > 0)
        {
            if (!Http2FrameCodec.TryReadFrame(remaining, out var frame, out var frameConsumed))
            {
                if (Http2FrameCodec.IsFrameTooLarge(remaining))
                {
                    await SendGoAwayAndCloseAsync(
                        connection,
                        Http2ErrorCode.FrameSizeError,
                        cancellationToken
                    );
                    return consumed;
                }

                return consumed;
            }

            consumed = remaining.GetPosition(frameConsumed);
            remaining = remaining.Slice(frameConsumed);

            try
            {
                // RFC 7540 §3.5: first frame after connection preface must be SETTINGS.
                var connState = GetRuntimeState(connection);
                if (!connState.ReceivedPostPrefaceFrame)
                {
                    connState.ReceivedPostPrefaceFrame = true;
                    if (frame!.Type != Http2FrameType.Settings)
                    {
                        await SendGoAwayAndCloseAsync(
                            connection,
                            Http2ErrorCode.ProtocolError,
                            cancellationToken
                        );
                        return consumed;
                    }
                }

                if (
                    await HandleFrameAsync(
                        connection,
                        frame!,
                        requestHandler,
                        logger,
                        cancellationToken
                    )
                )
                {
                    return consumed;
                }
            }
            finally
            {
                frame!.ReturnPayload();
            }
        }

        return consumed;
    }

    private static async ValueTask<bool> HandleFrameAsync(
        ITcpConnectionContext connection,
        Http2Frame frame,
        HttpRequestHandler requestHandler,
        ILogger? logger,
        CancellationToken cancellationToken
    )
    {
        // RFC 7540 §4.3: header blocks MUST be transmitted as a contiguous
        // sequence of frames. Any frame other than CONTINUATION while a
        // header block is pending is a connection error of type
        // PROTOCOL_ERROR.
        if (
            GetRuntimeState(connection).PendingContinuationStreamId is not null
            && frame.Type != Http2FrameType.Continuation
        )
        {
            await SendGoAwayAndCloseAsync(
                    connection,
                    Http2ErrorCode.ProtocolError,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return true;
        }

        switch (frame.Type)
        {
            case Http2FrameType.Settings:
                if (frame.StreamId != 0)
                {
                    await SendGoAwayAndCloseAsync(
                        connection,
                        Http2ErrorCode.ProtocolError,
                        cancellationToken
                    );
                    return true;
                }

                if (frame.HasFlag(Http2FrameFlags.Ack))
                {
                    if (frame.Length != 0)
                    {
                        await SendGoAwayAndCloseAsync(
                            connection,
                            Http2ErrorCode.ProtocolError,
                            cancellationToken
                        );
                        return true;
                    }

                    // ACK frames carry no settings payload — nothing to apply.
                    return false;
                }

                // RFC 7540 §6.5: SETTINGS payload must be a multiple of 6 bytes.
                if (frame.Length % 6 != 0)
                {
                    await SendGoAwayAndCloseAsync(
                        connection,
                        Http2ErrorCode.FrameSizeError,
                        cancellationToken
                    );
                    return true;
                }

                // Apply immediately per RFC 7540 §6.5 ("values MUST be processed
                // in the order received") — then ACK (§6.5.3: ACK after processing).
                var receivedSettings = Http2FrameCodec.ParseSettings(frame.Payload.Span);
                foreach (var setting in receivedSettings)
                {
                    // RFC 7540 §6.5.2: ENABLE_PUSH values other than 0 or 1
                    // are a connection error of type PROTOCOL_ERROR.
                    if (setting.Id == Http2SettingId.EnablePush && setting.Value > 1)
                    {
                        await SendGoAwayAndCloseAsync(
                                connection,
                                Http2ErrorCode.ProtocolError,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        return true;
                    }

                    // RFC 7540 §6.9.2: values above 2^31-1 are FLOW_CONTROL_ERROR.
                    if (
                        setting.Id == Http2SettingId.InitialWindowSize
                        && setting.Value > int.MaxValue
                    )
                    {
                        await SendGoAwayAndCloseAsync(
                            connection,
                            Http2ErrorCode.FlowControlError,
                            cancellationToken
                        );
                        return true;
                    }

                    // RFC 7540 §6.5.2: valid range is 2^14 .. 2^24-1.
                    if (
                        setting.Id == Http2SettingId.MaxFrameSize
                        && (setting.Value < 16384 || setting.Value > 16777215)
                    )
                    {
                        await SendGoAwayAndCloseAsync(
                            connection,
                            Http2ErrorCode.ProtocolError,
                            cancellationToken
                        );
                        return true;
                    }
                }
                GetRuntimeState(connection).ApplySettings(receivedSettings);
                // A SETTINGS_INITIAL_WINDOW_SIZE increase may have unblocked
                // response pumps stalled on flow-control backpressure.
                GetRuntimeState(connection).ReleaseAllFlowControlSignals();

                await connection.SendAsync(
                    new ReadOnlySequence<byte>(Http2FrameCodec.EncodeSettingsAck()),
                    cancellationToken
                );
                return false;

            case Http2FrameType.Ping:
                if (frame.StreamId != 0 || frame.Length != 8)
                {
                    await SendGoAwayAndCloseAsync(
                        connection,
                        Http2ErrorCode.ProtocolError,
                        cancellationToken
                    );
                    return true;
                }

                if (!frame.HasFlag(Http2FrameFlags.Ack))
                {
                    // Ping ack: small fixed-size frame, allocation is trivial
                    await connection.SendAsync(
                        new ReadOnlySequence<byte>(
                            Http2FrameCodec.EncodePing(frame.Payload.Span, ack: true)
                        ),
                        cancellationToken
                    );
                }

                return false;

            case Http2FrameType.Headers:
                return await Http2StreamHandler.ProcessHeadersFrame(
                    connection,
                    frame,
                    requestHandler,
                    logger,
                    cancellationToken
                );

            case Http2FrameType.Data:
                return await Http2StreamHandler.ProcessDataFrame(
                    connection,
                    frame,
                    requestHandler,
                    logger,
                    cancellationToken
                );

            case Http2FrameType.Priority:
                // RFC 7540 §6.3: PRIORITY MUST be associated with a stream.
                if (frame.StreamId == 0)
                {
                    await SendGoAwayAndCloseAsync(
                            connection,
                            Http2ErrorCode.ProtocolError,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return true;
                }

                // RFC 7540 §6.3: a PRIORITY frame with a length other than
                // 5 octets is a stream error of type FRAME_SIZE_ERROR.
                if (frame.Length != 5)
                {
                    await Http2StreamHandler
                        .SendRstStreamAsync(
                            connection,
                            frame.StreamId,
                            Http2ErrorCode.FrameSizeError,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return false;
                }

                // RFC 7540 §5.3.1: a stream cannot depend on itself.
                if (
                    Http2FrameCodec.TryGetStreamDependency(frame.Payload.Span, out var dependency)
                    && dependency == frame.StreamId
                )
                {
                    await Http2StreamHandler
                        .SendRstStreamAsync(
                            connection,
                            frame.StreamId,
                            Http2ErrorCode.ProtocolError,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return false;
                }

                return false;

            case Http2FrameType.PushPromise:
                // RFC 7540 §8.2: a server receiving PUSH_PROMISE (clients cannot
                // push) MUST treat it as a connection error of type PROTOCOL_ERROR.
                await SendGoAwayAndCloseAsync(
                    connection,
                    Http2ErrorCode.ProtocolError,
                    cancellationToken
                );
                return true;

            case Http2FrameType.Continuation:
                // Per RFC 7540 §6.10, CONTINUATION MUST continue an open
                // header block on the same stream.
                if (GetRuntimeState(connection).PendingContinuationStreamId is not int pendingId)
                {
                    await SendGoAwayAndCloseAsync(
                            connection,
                            Http2ErrorCode.ProtocolError,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return true;
                }

                if (frame.StreamId != pendingId)
                {
                    await SendGoAwayAndCloseAsync(
                        connection,
                        Http2ErrorCode.ProtocolError,
                        cancellationToken
                    );
                    return true;
                }
                return await Http2StreamHandler.ProcessHeadersFrame(
                    connection,
                    frame,
                    requestHandler,
                    logger,
                    cancellationToken
                );

            case Http2FrameType.GoAway:
                // RFC 7540 §6.8: stop accepting streams, drain active ones
                var goAwayState = GetRuntimeState(connection);
                goAwayState.GoAwayReceived = true;
                // Check if any streams are still active; if not, close immediately.
                if (goAwayState.Http2Streams is null || goAwayState.Http2Streams.Count == 0)
                {
                    connection.Close();
                }
                return true;

            case Http2FrameType.RstStream:
                return await Http2StreamHandler.ProcessRstStreamFrame(
                    connection,
                    frame,
                    cancellationToken
                );

            case Http2FrameType.WindowUpdate:
                return await Http2StreamHandler.ProcessWindowUpdateFrame(
                    connection,
                    frame,
                    cancellationToken
                );

            default:
                // RFC 7540 §5.5: unknown frame types MUST be ignored.
                return false;
        }
    }

    private static ConnectionRuntimeState GetRuntimeState(ITcpConnectionContext connection)
    {
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is null)
        {
            state = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };
            connection.UserState = state;
        }
        return state;
    }

    private static async ValueTask SendGoAwayAndCloseAsync(
        ITcpConnectionContext connection,
        Http2ErrorCode errorCode,
        CancellationToken cancellationToken
    )
    {
        var lastStreamId = 0;
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is not null)
            lastStreamId = state.HighestProcessedStreamId;

        var frame = Http2FrameCodec.EncodeGoAway(lastStreamId, errorCode);
        await connection
            .SendAsync(new ReadOnlySequence<byte>(frame), cancellationToken)
            .ConfigureAwait(false);
        connection.Close();
    }
}
