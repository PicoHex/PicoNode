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
                // Per RFC 7540 §6.10, CONTINUATION MUST be on the same stream as the HEADERS it continues.
                if (
                    GetRuntimeState(connection).PendingContinuationStreamId is int pendingId
                    && frame.StreamId != pendingId
                )
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
