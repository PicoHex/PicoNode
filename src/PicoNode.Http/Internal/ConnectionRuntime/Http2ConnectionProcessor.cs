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
            var settings = (ReadOnlySpan<Http2Setting>)
            [
                new(Http2SettingId.MaxConcurrentStreams, 100),
                new(Http2SettingId.InitialWindowSize, 65535),
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

            if (await HandleFrameAsync(connection, frame!, requestHandler, logger, cancellationToken))
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

                    return false;
                }

                // SettingsAck: small fixed-size frame, allocation is trivial
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
            case Http2FrameType.Priority:
                return false;

            case Http2FrameType.PushPromise:
            case Http2FrameType.Continuation:
                await SendGoAwayAndCloseAsync(
                    connection,
                    Http2ErrorCode.Http11Required,
                    cancellationToken
                );
                return true;

            case Http2FrameType.GoAway:
                connection.Close();
                return true;

            case Http2FrameType.RstStream:
            case Http2FrameType.WindowUpdate:
            default:
                await SendGoAwayAndCloseAsync(
                    connection,
                    Http2ErrorCode.ProtocolError,
                    cancellationToken
                );
                return true;
        }
    }

    private static async ValueTask SendGoAwayAndCloseAsync(
        ITcpConnectionContext connection,
        Http2ErrorCode errorCode,
        CancellationToken cancellationToken
    )
    {
        var frame = Http2FrameCodec.EncodeGoAway(0, errorCode);
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), cancellationToken);
        connection.Close();
    }
}
