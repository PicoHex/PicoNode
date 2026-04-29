namespace PicoNode.Http.Internal.ConnectionRuntime;

using System.Buffers;

internal static class WebSocketMessageProcessor
{
    public static async ValueTask<SequencePosition> ProcessAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        WebSocketMessageHandler? handler,
        CancellationToken cancellationToken,
        WebSocketMessageProcessorState? state = null
    )
    {
        var currentState = state ?? new WebSocketMessageProcessorState();
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
                    currentState.MessageOpCode = null;
                    currentState.PayloadBuffer.Clear();
                    return consumed;
                }
                case WebSocketOpCode.Pong:
                    break;

                case WebSocketOpCode.Text:
                case WebSocketOpCode.Binary:
                    currentState.MessageOpCode = frame.OpCode;
                    currentState.PayloadBuffer.Clear();
                    currentState.PayloadBuffer.Write(frame.Payload.Span);

                    if (frame.Fin && handler is not null)
                    {
                        await handler(
                            new WebSocketMessage(
                                currentState.MessageOpCode.Value,
                                currentState.PayloadBuffer.WrittenMemory.ToArray(),
                                true
                            ),
                            connection,
                            cancellationToken
                        );
                        currentState.MessageOpCode = null;
                        currentState.PayloadBuffer.Clear();
                    }
                    break;

                case WebSocketOpCode.Continuation:
                    if (currentState.MessageOpCode is null)
                        break;

                    currentState.PayloadBuffer.Write(frame.Payload.Span);

                    if (frame.Fin && handler is not null)
                    {
                        await handler(
                            new WebSocketMessage(
                                currentState.MessageOpCode.Value,
                                currentState.PayloadBuffer.WrittenMemory.ToArray(),
                                true
                            ),
                            connection,
                            cancellationToken
                        );
                        currentState.MessageOpCode = null;
                        currentState.PayloadBuffer.Clear();
                    }
                    break;

                default:
                    break;
            }
        }

        return consumed;
    }
}
