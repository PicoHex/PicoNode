namespace PicoNode.Http.Internal.ConnectionRuntime;

using System.Buffers;

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
                {
                    var size = WebSocketFrameCodec.MeasureFrameSize(
                        frame.Payload.Length
                    );
                    var rented = ArrayPool<byte>.Shared.Rent(size);
                    try
                    {
                        WebSocketFrameCodec.WriteFrame(
                            rented,
                            WebSocketOpCode.Pong,
                            frame.Payload.Span
                        );
                        await connection.SendAsync(
                            new ReadOnlySequence<byte>(rented.AsMemory(0, size)),
                            cancellationToken
                        );
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }

                    break;
                }
                case WebSocketOpCode.Close:
                {
                    var size = WebSocketFrameCodec.MeasureFrameSize(
                        frame.Payload.Length
                    );
                    var rented = ArrayPool<byte>.Shared.Rent(size);
                    try
                    {
                        WebSocketFrameCodec.WriteFrame(
                            rented,
                            WebSocketOpCode.Close,
                            frame.Payload.Span
                        );
                        await connection.SendAsync(
                            new ReadOnlySequence<byte>(rented.AsMemory(0, size)),
                            cancellationToken
                        );
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }

                    connection.Close();
                    return consumed;
                }
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
