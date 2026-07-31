namespace PicoNode.Http.Internal.ConnectionRuntime;

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
            if (
                !WebSocketFrameCodec.TryReadFrame(
                    remaining,
                    out var frame,
                    out var frameConsumed,
                    currentState.MaxMessageSize
                )
            )
            {
                if (frameConsumed < 0)
                {
                    // Frame declares a payload larger than the configured maximum — close 1009.
                    await CloseWithCodeAsync(connection, 1009, cancellationToken)
                        .ConfigureAwait(false);
                    return consumed;
                }
                return consumed;
            }

            consumed = remaining.GetPosition(frameConsumed);
            remaining = remaining.Slice(frameConsumed);

            // Update activity timestamp for heartbeat tracking
            currentState.LastFrameReceivedAt = DateTime.UtcNow;

            // RFC 6455 §5.1: client-to-server frames must be masked
            if (!frame!.Masked)
            {
                await CloseWithProtocolError(connection, cancellationToken).ConfigureAwait(false);
                return consumed;
            }

            // RFC 6455 §5.5: control frames must have FIN=1 and payload �?125
            if (
                frame.OpCode
                is WebSocketOpCode.Ping
                    or WebSocketOpCode.Pong
                    or WebSocketOpCode.Close
            )
            {
                if (!frame.Fin)
                {
                    await CloseWithProtocolError(connection, cancellationToken)
                        .ConfigureAwait(false);
                    return consumed;
                }
                if (frame.Payload.Length > 125)
                {
                    await CloseWithProtocolError(connection, cancellationToken)
                        .ConfigureAwait(false);
                    return consumed;
                }
            }

            switch (frame.OpCode)
            {
                case WebSocketOpCode.Ping:
                {
                    var size = WebSocketFrameCodec.MeasureFrameSize(frame.Payload.Length);
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
                    var size = WebSocketFrameCodec.MeasureFrameSize(frame.Payload.Length);
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
                    // RFC 6455 §5.4: new data message during fragmentation = protocol error
                    if (currentState.MessageOpCode is not null)
                    {
                        await CloseWithProtocolError(connection, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;
                    }
                    currentState.MessageOpCode = frame.OpCode;
                    currentState.PayloadBuffer.Clear();

                    var payload1 = frame.Rsv1
                        ? currentState.Decompress(frame.Payload.Span)
                        : frame.Payload.ToArray();
                    currentState.PayloadBuffer.Write(payload1);

                    if (currentState.PayloadBuffer.WrittenCount > currentState.MaxMessageSize)
                    {
                        await CloseWithProtocolError(connection, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;
                    }

                    if (frame.Fin && handler is not null)
                    {
                        var messageBytes = currentState.PayloadBuffer.WrittenMemory.ToArray();
                        // RFC 6455 §8.1: Text messages must be valid UTF-8
                        if (
                            currentState.MessageOpCode == WebSocketOpCode.Text
                            && !IsValidUtf8(messageBytes)
                        )
                        {
                            await CloseWithProtocolError(connection, cancellationToken)
                                .ConfigureAwait(false);
                            return consumed;
                        }
                        await handler(
                            new WebSocketMessage(
                                currentState.MessageOpCode.Value,
                                messageBytes,
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
                    // RFC 6455 §5.4: continuation without start = protocol error
                    if (currentState.MessageOpCode is null)
                    {
                        await CloseWithProtocolError(connection, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;
                    }

                    var payload2 = frame.Rsv1
                        ? currentState.Decompress(frame.Payload.Span)
                        : frame.Payload.ToArray();
                    currentState.PayloadBuffer.Write(payload2);

                    if (currentState.PayloadBuffer.WrittenCount > currentState.MaxMessageSize)
                    {
                        await CloseWithProtocolError(connection, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;
                    }

                    if (frame.Fin && handler is not null)
                    {
                        var messageBytes = currentState.PayloadBuffer.WrittenMemory.ToArray();
                        if (
                            currentState.MessageOpCode == WebSocketOpCode.Text
                            && !IsValidUtf8(messageBytes)
                        )
                        {
                            await CloseWithProtocolError(connection, cancellationToken)
                                .ConfigureAwait(false);
                            return consumed;
                        }
                        await handler(
                            new WebSocketMessage(
                                currentState.MessageOpCode.Value,
                                messageBytes,
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

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static bool IsValidUtf8(byte[] data)
    {
        if (data.Length == 0)
            return true;
        try
        {
            // Encoding.UTF8 default uses a replacement fallback (never throws);
            // the strict decoder throws on any invalid sequence (RFC 6455 §8.1).
            StrictUtf8.GetCharCount(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static async ValueTask CloseWithCodeAsync(
        ITcpConnectionContext connection,
        ushort code,
        CancellationToken ct
    )
    {
        byte[] payload = [(byte)(code >> 8), (byte)code];
        var size = WebSocketFrameCodec.MeasureFrameSize(payload.Length);
        var rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            WebSocketFrameCodec.WriteFrame(rented, WebSocketOpCode.Close, payload);
            await connection
                .SendAsync(new ReadOnlySequence<byte>(rented.AsMemory(0, size)), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        connection.Close();
    }

    private static async ValueTask CloseWithProtocolError(
        ITcpConnectionContext connection,
        CancellationToken ct
    )
    {
        var size = WebSocketFrameCodec.MeasureFrameSize(0);
        var rented = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            WebSocketFrameCodec.WriteFrame(rented, WebSocketOpCode.Close, []);
            await connection
                .SendAsync(new ReadOnlySequence<byte>(rented.AsMemory(0, size)), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        connection.Close();
    }
}
