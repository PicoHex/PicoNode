namespace PicoNode.Http.Internal.ConnectionRuntime;

internal static partial class Http2StreamHandler
{
    internal static async ValueTask SendRstStreamAsync(
        ITcpConnectionContext connection,
        int streamId,
        Http2ErrorCode errorCode,
        CancellationToken ct
    )
    {
        // RST_STREAM carries a 4-byte error code. Write it into a pooled frame
        // buffer (same pattern as the DATA/WINDOW_UPDATE writers) instead of
        // allocating a payload array plus an encoded frame per reset.
        var totalSize = Http2FrameCodec.FrameHeaderSize + 4;
        var rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            Span<byte> payload = stackalloc byte[4];
            payload[0] = (byte)(((int)errorCode >> 24) & 0xFF);
            payload[1] = (byte)(((int)errorCode >> 16) & 0xFF);
            payload[2] = (byte)(((int)errorCode >> 8) & 0xFF);
            payload[3] = (byte)((int)errorCode & 0xFF);

            Http2FrameCodec.WriteFrame(
                rented,
                Http2FrameType.RstStream,
                Http2FrameFlags.None,
                streamId,
                payload
            );
            await connection
                .SendAsync(new ReadOnlySequence<byte>(rented.AsMemory(0, totalSize)), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        // Remove the stream from tracking and stop any response pump that is
        // still writing to it. Without the cancel, a streaming response keeps
        // emitting DATA frames on a stream the server just reset, and the
        // untracked stream turns the peer's next WINDOW_UPDATE into a GOAWAY.
        var state = connection.UserState as ConnectionRuntimeState;
        if (state?.Http2Streams?.TryRemove(streamId, out var removed) == true)
        {
            removed.Aborted = true;
            removed.ResponseCts?.Cancel();
        }
    }

    private static async ValueTask SendGoAwayAndCloseAsync(
        ITcpConnectionContext connection,
        Http2ErrorCode errorCode,
        CancellationToken ct
    )
    {
        // Use the highest processed stream ID for graceful shutdown signalling.
        var lastStreamId = 0;
        var state = connection.UserState as ConnectionRuntimeState;
        if (state is not null)
            lastStreamId = state.HighestProcessedStreamId;

        var frame = Http2FrameCodec.EncodeGoAway(lastStreamId, errorCode);
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct).ConfigureAwait(false);
        connection.Close();
    }

    // ── Shared frame-writing helpers ─────────────────────────────────────

    /// <summary>
    /// Writes one complete header block — a HEADERS frame plus, when the encoded
    /// block exceeds the peer's max frame size, every one of its CONTINUATION
    /// frames — from ONE rented buffer in ONE <c>SendAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 9113 §6.2/§6.10 require a header block to be transmitted as a
    /// contiguous sequence: no other frame may appear between HEADERS and its
    /// CONTINUATION frames. The transport serialises every send with its own
    /// send gate, so issuing the whole block as a single send makes it atomic on
    /// the wire: a concurrent DATA pump, the frame loop's PING ACK / SETTINGS ACK
    /// / WINDOW_UPDATE / RST_STREAM can only be written entirely before or
    /// entirely after the block, never inside it. Writing one frame per send —
    /// the previous shape — could not offer that, because every await between
    /// two frames was a window for an interleaving frame.
    /// </para>
    /// <para>
    /// Must be called with <c>ConnectionRuntimeState.ResponseHpackEncoderLock</c>
    /// held: the block was just encoded against the shared encoder's dynamic
    /// table, and a block encoded later may only reach the wire after the blocks
    /// encoded before it (otherwise the peer decodes a forward reference).
    /// </para>
    /// </remarks>
    private static async ValueTask SendHeaderBlockAsync(
        ITcpConnectionContext connection,
        int streamId,
        ReadOnlyMemory<byte> encodedHeaders,
        Http2FrameFlags flags,
        CancellationToken ct
    )
    {
        var state = connection.UserState as ConnectionRuntimeState;
        var maxFrameSize = Math.Max(
            state?.RemoteMaxFrameSize ?? Http2FrameCodec.DefaultMaxFrameSize,
            Http2FrameCodec.DefaultMaxFrameSize
        );

        if (encodedHeaders.Length <= maxFrameSize)
        {
            var totalSize = Http2FrameCodec.FrameHeaderSize + encodedHeaders.Length;
            var rented = ArrayPool<byte>.Shared.Rent(totalSize);
            try
            {
                Http2FrameCodec.WriteFrame(
                    rented.AsSpan(0, totalSize),
                    Http2FrameType.Headers,
                    flags,
                    streamId,
                    encodedHeaders.Span
                );
                await connection
                    .SendAsync(new ReadOnlySequence<byte>(rented.AsMemory(0, totalSize)), ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
            return;
        }

        // Split: HEADERS without END_HEADERS, then CONTINUATION frames with
        // END_HEADERS on the last one. Other flags (e.g. END_STREAM) stay on the
        // HEADERS frame only. Every frame is assembled into the same buffer, so
        // the block leaves in one send.
        var frameCount = (encodedHeaders.Length + maxFrameSize - 1) / maxFrameSize;
        // Every chunk carries at most maxFrameSize payload bytes and costs one
        // 9-byte frame header, so the total stays below 2 × length and cannot
        // overflow for any block the server can encode.
        var totalLength = encodedHeaders.Length + frameCount * Http2FrameCodec.FrameHeaderSize;
        var blockBuffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            var destination = blockBuffer.AsMemory(0, totalLength);
            var destinationSpan = destination.Span;
            var readOffset = 0;
            var writeOffset = 0;
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var chunkLength = Math.Min(encodedHeaders.Length - readOffset, maxFrameSize);
                var isLast = frameIndex == frameCount - 1;
                var frameType =
                    frameIndex == 0 ? Http2FrameType.Headers : Http2FrameType.Continuation;
                var frameFlags =
                    frameIndex == 0 ? flags & ~Http2FrameFlags.EndHeaders : Http2FrameFlags.None;
                if (isLast)
                    frameFlags |= Http2FrameFlags.EndHeaders;

                Http2FrameCodec.WriteFrameHeader(
                    destinationSpan.Slice(writeOffset),
                    chunkLength,
                    frameType,
                    frameFlags,
                    streamId
                );
                encodedHeaders
                    .Span.Slice(readOffset, chunkLength)
                    .CopyTo(destinationSpan.Slice(writeOffset + Http2FrameCodec.FrameHeaderSize));

                readOffset += chunkLength;
                writeOffset += Http2FrameCodec.FrameHeaderSize + chunkLength;
            }

            await connection
                .SendAsync(new ReadOnlySequence<byte>(destination), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>Writes a DATA frame with the given payload using a pooled buffer.</summary>
    private static async ValueTask WriteDataFrameAsync(
        ITcpConnectionContext connection,
        int streamId,
        Http2FrameFlags flags,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct
    )
    {
        var totalSize = Http2FrameCodec.FrameHeaderSize + payload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            Http2FrameCodec.WriteFrame(rented, Http2FrameType.Data, flags, streamId, payload.Span);
            await connection.SendAsync(
                new ReadOnlySequence<byte>(rented.AsMemory(0, totalSize)),
                ct
            );
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async ValueTask WriteWindowUpdateFrameAsync(
        ITcpConnectionContext connection,
        int streamId,
        int increment,
        CancellationToken ct
    )
    {
        var totalSize = Http2FrameCodec.FrameHeaderSize + 4;
        var rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            Span<byte> payload = stackalloc byte[4];
            payload[0] = (byte)((increment >> 24) & 0x7F);
            payload[1] = (byte)((increment >> 16) & 0xFF);
            payload[2] = (byte)((increment >> 8) & 0xFF);
            payload[3] = (byte)(increment & 0xFF);

            var dest = rented.AsSpan(0, totalSize);
            Http2FrameCodec.WriteFrame(
                dest,
                Http2FrameType.WindowUpdate,
                Http2FrameFlags.None,
                streamId,
                payload
            );
            await connection.SendAsync(
                new ReadOnlySequence<byte>(rented.AsMemory(0, totalSize)),
                ct
            );
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
