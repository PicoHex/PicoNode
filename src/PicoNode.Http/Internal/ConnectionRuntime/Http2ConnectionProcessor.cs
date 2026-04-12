namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static class Http2ConnectionProcessor
{
    public static async ValueTask<SequencePosition> ProcessAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        bool sendInitialSettings,
        CancellationToken cancellationToken
    )
    {
        var remaining = buffer;
        var consumed = buffer.Start;

        if (sendInitialSettings)
        {
            await SendFrameAsync(
                connection,
                Http2FrameCodec.EncodeSettings(
                    new Http2Setting(Http2SettingId.MaxConcurrentStreams, 100),
                    new Http2Setting(Http2SettingId.InitialWindowSize, 65535)
                ),
                cancellationToken
            );
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

            if (await HandleFrameAsync(connection, frame!, cancellationToken))
            {
                return consumed;
            }
        }

        return consumed;
    }

    private static async ValueTask<bool> HandleFrameAsync(
        ITcpConnectionContext connection,
        Http2Frame frame,
        CancellationToken cancellationToken
    )
    {
        switch (frame.Type)
        {
            case Http2FrameType.Settings:
                if (frame.StreamId != 0)
                {
                    await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, cancellationToken);
                    return true;
                }

                if (frame.HasFlag(Http2FrameFlags.Ack))
                {
                    if (frame.Length != 0)
                    {
                        await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, cancellationToken);
                        return true;
                    }

                    return false;
                }

                await SendFrameAsync(connection, Http2FrameCodec.EncodeSettingsAck(), cancellationToken);
                return false;

            case Http2FrameType.Ping:
                if (frame.StreamId != 0 || frame.Length != 8)
                {
                    await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, cancellationToken);
                    return true;
                }

                if (!frame.HasFlag(Http2FrameFlags.Ack))
                {
                    await SendFrameAsync(
                        connection,
                        Http2FrameCodec.EncodePing(frame.Payload.Span, ack: true),
                        cancellationToken
                    );
                }

                return false;

            case Http2FrameType.Headers:
            case Http2FrameType.Data:
            case Http2FrameType.Priority:
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
                await SendGoAwayAndCloseAsync(connection, Http2ErrorCode.ProtocolError, cancellationToken);
                return true;
        }
    }

    private static async ValueTask SendGoAwayAndCloseAsync(
        ITcpConnectionContext connection,
        Http2ErrorCode errorCode,
        CancellationToken cancellationToken
    )
    {
        await SendFrameAsync(connection, Http2FrameCodec.EncodeGoAway(0, errorCode), cancellationToken);
        connection.Close();
    }

    private static Task SendFrameAsync(
        ITcpConnectionContext connection,
        byte[] frame,
        CancellationToken cancellationToken
    ) => connection.SendAsync(new ReadOnlySequence<byte>(frame), cancellationToken);
}
