namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static class WebSocketConnectionProcessor
{
    public static async ValueTask<SequencePosition> ProcessAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var remaining = buffer;
        var consumed = buffer.Start;

        while (remaining.Length > 0)
        {
            if (!WebSocketFrameCodec.TryReadFrame(remaining, out var frame, out var frameConsumed))
            {
                return consumed;
            }

            consumed = remaining.GetPosition(frameConsumed);
            remaining = remaining.Slice(frameConsumed);

            switch (frame!.OpCode)
            {
                case WebSocketOpCode.Ping:
                    await connection.SendAsync(
                        new ReadOnlySequence<byte>(
                            WebSocketFrameCodec.EncodeFrame(
                                WebSocketOpCode.Pong,
                                frame.Payload.Span
                            )
                        ),
                        cancellationToken
                    );
                    break;
                case WebSocketOpCode.Close:
                    await connection.SendAsync(
                        new ReadOnlySequence<byte>(
                            WebSocketFrameCodec.EncodeFrame(
                                WebSocketOpCode.Close,
                                frame.Payload.Span
                            )
                        ),
                        cancellationToken
                    );
                    connection.Close();
                    return consumed;
                case WebSocketOpCode.Pong:
                case WebSocketOpCode.Text:
                case WebSocketOpCode.Binary:
                case WebSocketOpCode.Continuation:
                default:
                    break;
            }
        }

        return consumed;
    }
}
