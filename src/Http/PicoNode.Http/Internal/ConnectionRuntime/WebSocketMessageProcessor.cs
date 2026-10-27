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
                !WebSocketFrameCodec.TryReadFrameHeader(
                    remaining,
                    out var header,
                    out var headerError,
                    currentState.MaxMessageSize
                )
            )
            {
                switch (headerError)
                {
                    // Payload larger than the configured maximum (RFC 6455 §7.4.1).
                    case WebSocketFrameHeaderError.TooLarge:
                        await CloseWithCodeAsync(connection, 1009, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;

                    // A length encoded in more bytes than RFC 6455 §5.2 allows is a
                    // protocol error, not a size error.
                    case WebSocketFrameHeaderError.NonMinimalLength:
                        await CloseWithCodeAsync(connection, 1002, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;

                    default:
                        return consumed;
                }
            }

            consumed = remaining.GetPosition(header.FrameLength);
            remaining = remaining.Slice(header.FrameLength);

            // Update activity timestamp for heartbeat tracking
            currentState.LastFrameReceivedAt = DateTime.UtcNow;

            // RFC 6455 §5.1: client-to-server frames must be masked
            if (!header.Masked)
            {
                await CloseWithProtocolError(connection, cancellationToken).ConfigureAwait(false);
                return consumed;
            }

            // RFC 6455 §5.2: RSV2/RSV3 must be 0 — no negotiated extension uses
            // them, so a set bit is a protocol error.
            if (header.Rsv2 || header.Rsv3)
            {
                await CloseWithProtocolError(connection, cancellationToken).ConfigureAwait(false);
                return consumed;
            }

            // RFC 7692 §5.2: RSV1 is only legal once permessage-deflate was
            // negotiated. Accepting it otherwise silently treats arbitrary
            // client data as a compressed message.
            if (header.Rsv1 && !currentState.CompressionNegotiated)
            {
                await CloseWithProtocolError(connection, cancellationToken).ConfigureAwait(false);
                return consumed;
            }

            // RFC 6455 §5.5: control frames must have FIN=1 and payload ≤ 125
            if (
                header.OpCode
                is WebSocketOpCode.Ping
                    or WebSocketOpCode.Pong
                    or WebSocketOpCode.Close
            )
            {
                if (!header.Fin)
                {
                    await CloseWithProtocolError(connection, cancellationToken)
                        .ConfigureAwait(false);
                    return consumed;
                }
                if (header.Payload.Length > 125)
                {
                    await CloseWithProtocolError(connection, cancellationToken)
                        .ConfigureAwait(false);
                    return consumed;
                }

                // RFC 7692 §6.1: RSV1 must be 0 on control frames.
                if (header.Rsv1)
                {
                    await CloseWithProtocolError(connection, cancellationToken)
                        .ConfigureAwait(false);
                    return consumed;
                }
            }

            switch (header.OpCode)
            {
                case WebSocketOpCode.Ping:
                {
                    // Control payloads are ≤ 125 bytes; the small array is fine.
                    var payload = WebSocketFrameCodec.MaterializePayload(header);
                    var size = WebSocketFrameCodec.MeasureFrameSize(payload.Length);
                    var rented = ArrayPool<byte>.Shared.Rent(size);
                    try
                    {
                        WebSocketFrameCodec.WriteFrame(rented, WebSocketOpCode.Pong, payload);
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
                    var payload = WebSocketFrameCodec.MaterializePayload(header);

                    // RFC 6455 §5.5.1 + §7.4: payload must be empty or
                    // code (2 bytes, valid range) + UTF-8 reason.
                    if (payload.Length == 1)
                    {
                        await CloseWithCodeAsync(connection, 1002, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;
                    }

                    if (payload.Length >= 2)
                    {
                        var closeCode = (payload[0] << 8) | payload[1];
                        var reason = payload.AsSpan(2);
                        if (
                            !IsValidCloseCode(closeCode)
                            || (reason.Length > 0 && !IsValidUtf8(reason.ToArray()))
                        )
                        {
                            await CloseWithCodeAsync(connection, 1002, cancellationToken)
                                .ConfigureAwait(false);
                            return consumed;
                        }
                    }

                    var size = WebSocketFrameCodec.MeasureFrameSize(payload.Length);
                    var rented = ArrayPool<byte>.Shared.Rent(size);
                    try
                    {
                        WebSocketFrameCodec.WriteFrame(rented, WebSocketOpCode.Close, payload);
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
                    currentState.MessageOpCode = header.OpCode;
                    currentState.PayloadBuffer.Clear();

                    // RSV1 is only valid on the FIRST frame of a message (RFC 7692 §6);
                    // record it and buffer the RAW payload — decompression must happen
                    // once over the whole reassembled message, not per fragment.
                    // Payload streams straight into the reassembly buffer (one copy,
                    // no per-frame array — data frames can be MaxMessageSize-sized,
                    // i.e. LOH-allocated by the old code).
                    currentState.MessageCompressed = header.Rsv1;
                    WebSocketFrameCodec.AppendPayload(
                        currentState.PayloadBuffer,
                        header.Payload,
                        header.Masked,
                        header.MaskKey
                    );

                    // RFC 6455 §7.4.1: the reassembled message exceeded the
                    // configured limit — that is 1009 (Message Too Big), not a
                    // protocol error.
                    if (currentState.PayloadBuffer.WrittenCount > currentState.MaxMessageSize)
                    {
                        await CloseWithCodeAsync(connection, 1009, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;
                    }

                    if (header.Fin)
                    {
                        if (handler is not null)
                        {
                            // Delivery can close the connection (1002/1009); nothing
                            // after that close may be processed from this buffer.
                            if (
                                !await DeliverMessageAsync(
                                        connection,
                                        currentState,
                                        handler,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                            )
                            {
                                return consumed;
                            }
                        }
                        else
                        {
                            // No handler: drop the completed message but reset
                            // fragment state — otherwise the NEXT data frame is
                            // rejected as "new message during fragmentation".
                            currentState.MessageOpCode = null;
                            currentState.PayloadBuffer.Clear();
                        }
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

                    // RFC 7692 §6: RSV1 is only valid on the FIRST frame of a message.
                    if (header.Rsv1)
                    {
                        await CloseWithProtocolError(connection, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;
                    }

                    WebSocketFrameCodec.AppendPayload(
                        currentState.PayloadBuffer,
                        header.Payload,
                        header.Masked,
                        header.MaskKey
                    );

                    // RFC 6455 §7.4.1: the reassembled message exceeded the
                    // configured limit — that is 1009 (Message Too Big), not a
                    // protocol error.
                    if (currentState.PayloadBuffer.WrittenCount > currentState.MaxMessageSize)
                    {
                        await CloseWithCodeAsync(connection, 1009, cancellationToken)
                            .ConfigureAwait(false);
                        return consumed;
                    }

                    if (header.Fin)
                    {
                        if (handler is not null)
                        {
                            if (
                                !await DeliverMessageAsync(
                                        connection,
                                        currentState,
                                        handler,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                            )
                            {
                                return consumed;
                            }
                        }
                        else
                        {
                            // No handler: drop the completed message but reset
                            // fragment state so the next data frame starts a
                            // new message (mirrors the Text/Binary path).
                            currentState.MessageOpCode = null;
                            currentState.PayloadBuffer.Clear();
                        }
                    }
                    break;

                default:
                    // RFC 6455 §5.2: unknown opcodes MUST fail the connection
                    // (reserved non-control 0x3-0x7, reserved control 0xB-0xF).
                    await CloseWithProtocolError(connection, cancellationToken)
                        .ConfigureAwait(false);
                    return consumed;
            }
        }

        return consumed;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Validates close codes per RFC 6455 §7.4.1 plus the IANA WebSocket Close
    /// Code Number registry: 1000-1003, 1007-1014 (1012 Service Restart,
    /// 1013 Try Again Later, 1014 Bad Gateway) and the private 3000-4999 range.
    /// 1004-1006 and 1015 are reserved and MUST NOT appear on the wire.
    /// </summary>
    private static bool IsValidCloseCode(int code) =>
        code is (>= 1000 and <= 1003) or (>= 1007 and <= 1014) or (>= 3000 and <= 4999);

    /// <summary>
    /// Decompresses (when the message is permessage-deflate compressed) the fully
    /// reassembled payload, validates UTF-8 for text messages, and delivers it.
    /// Returns <c>false</c> when the message was rejected and the connection was
    /// closed — the caller must then stop consuming the buffer.
    /// </summary>
    private static async ValueTask<bool> DeliverMessageAsync(
        ITcpConnectionContext connection,
        WebSocketMessageProcessorState currentState,
        WebSocketMessageHandler handler,
        CancellationToken ct
    )
    {
        byte[] messageBytes;
        try
        {
            messageBytes = currentState.MessageCompressed
                ? currentState.Decompress(
                    currentState.PayloadBuffer.WrittenMemory.Span,
                    currentState.MaxMessageSize
                )
                : currentState.PayloadBuffer.WrittenMemory.ToArray();
        }
        catch (InvalidDataException)
        {
            // Zip bomb: compressed payload inflates beyond MaxMessageSize.
            // RFC 6455 §7.4.1: close with 1009 (Message Too Big).
            await CloseWithCodeAsync(connection, 1009, ct).ConfigureAwait(false);
            currentState.MessageOpCode = null;
            currentState.PayloadBuffer.Clear();
            return false;
        }

        // RFC 6455 §8.1: Text messages must be valid UTF-8. §7.4.1: a payload
        // that is not valid data is 1007 (Invalid frame payload data), not a
        // protocol error (1002).
        if (currentState.MessageOpCode == WebSocketOpCode.Text && !IsValidUtf8(messageBytes))
        {
            await CloseWithCodeAsync(connection, 1007, ct).ConfigureAwait(false);
            currentState.MessageOpCode = null;
            currentState.PayloadBuffer.Clear();
            return false;
        }

        await handler(
                new WebSocketMessage(currentState.MessageOpCode!.Value, messageBytes, true),
                connection,
                ct
            )
            .ConfigureAwait(false);
        currentState.MessageOpCode = null;
        currentState.PayloadBuffer.Clear();
        return true;
    }

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

    /// <summary>
    /// Fails the WebSocket connection for a protocol error (RFC 6455 §5.1, §5.2,
    /// §5.4, §5.5). §7.4.1: the close frame must carry 1002 (Protocol Error) —
    /// an empty close body signals "no status", which the Autobahn fuzzing
    /// suite reports as a missing close code (case 3.x).
    /// </summary>
    private static ValueTask CloseWithProtocolError(
        ITcpConnectionContext connection,
        CancellationToken ct
    ) => CloseWithCodeAsync(connection, 1002, ct);
}
