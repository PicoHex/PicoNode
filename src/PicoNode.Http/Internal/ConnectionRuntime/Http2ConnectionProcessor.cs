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

        // RFC 7540 §3.4: client connection preface (PRI * HTTP/2.0...).
        // For ALPN, the preface is also sent by browsers (optional per spec but common).
        var prefaceLen = Http2FrameCodec.ClientPreface.Length;
        if (buffer.Length >= prefaceLen)
        {
            Span<byte> preface = stackalloc byte[prefaceLen];
            buffer.Slice(0, prefaceLen).CopyTo(preface);
            if (preface.SequenceEqual(Http2FrameCodec.ClientPreface))
            {
                buffer = buffer.Slice(prefaceLen);
                consumed = buffer.Start;
            }
        }

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

                    // Apply any pending settings now that ACK is received
                    var ackState = GetRuntimeState(connection);
                    if (ackState.PendingSettings is not null)
                    {
                        ackState.ApplySettings(ackState.PendingSettings);
                        ackState.PendingSettings = null;
                    }
                    return false;
                }

                // Buffer settings until ACK, per RFC 7540 §6.5.3
                var receivedSettings = Http2FrameCodec.ParseSettings(frame.Payload.Span);
                GetRuntimeState(connection).PendingSettings = receivedSettings;

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
                await SendGoAwayAndCloseAsync(
                    connection,
                    Http2ErrorCode.Http11Required,
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
                await SendGoAwayAndCloseAsync(
                    connection,
                    Http2ErrorCode.ProtocolError,
                    cancellationToken
                );
                return true;
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
