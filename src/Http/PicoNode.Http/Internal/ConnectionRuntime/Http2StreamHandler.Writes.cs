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
        // RST_STREAM has a 4-byte payload for the error code
        var payload = new byte[4];
        payload[0] = (byte)(((int)errorCode >> 24) & 0xFF);
        payload[1] = (byte)(((int)errorCode >> 16) & 0xFF);
        payload[2] = (byte)(((int)errorCode >> 8) & 0xFF);
        payload[3] = (byte)((int)errorCode & 0xFF);

        var frame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.RstStream,
            Http2FrameFlags.None,
            streamId,
            payload
        );
        await connection.SendAsync(new ReadOnlySequence<byte>(frame), ct).ConfigureAwait(false);

        // Remove the stream from tracking
        var state = connection.UserState as ConnectionRuntimeState;
        state?.Http2Streams?.TryRemove(streamId, out _);
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

    /// <summary>Writes a HEADERS frame with HPACK-encoded headers using a pooled buffer.
    /// When the encoded block exceeds the peer's max frame size, it is split across
    /// CONTINUATION frames (RFC 7540 §6.2/§6.10).</summary>
    private static async ValueTask WriteHeadersFrameAsync(
        ITcpConnectionContext connection,
        int streamId,
        Http2FrameFlags flags,
        ReadOnlyMemory<byte> encodedHeaders,
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
                Http2FrameCodec.WriteFrameHeader(
                    rented,
                    encodedHeaders.Length,
                    Http2FrameType.Headers,
                    flags,
                    streamId
                );
                encodedHeaders.Span.CopyTo(rented.AsSpan(Http2FrameCodec.FrameHeaderSize));
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(rented.AsMemory(0, totalSize)),
                    ct
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
            return;
        }

        // Split: HEADERS without END_HEADERS, then CONTINUATION frames.
        // END_HEADERS goes on the last frame; other flags (e.g. END_STREAM)
        // stay on the HEADERS frame only.
        var offset = 0;
        var isFirst = true;
        while (offset < encodedHeaders.Length)
        {
            var chunkLength = Math.Min(encodedHeaders.Length - offset, maxFrameSize);
            var isLast = offset + chunkLength >= encodedHeaders.Length;
            var frameType = isFirst ? Http2FrameType.Headers : Http2FrameType.Continuation;
            var frameFlags =
                frameType == Http2FrameType.Headers
                    ? flags & ~Http2FrameFlags.EndHeaders
                    : Http2FrameFlags.None;
            if (isLast)
                frameFlags |= Http2FrameFlags.EndHeaders;

            var frameSize = Http2FrameCodec.FrameHeaderSize + chunkLength;
            var rented = ArrayPool<byte>.Shared.Rent(frameSize);
            try
            {
                Http2FrameCodec.WriteFrameHeader(
                    rented,
                    chunkLength,
                    frameType,
                    frameFlags,
                    streamId
                );
                encodedHeaders
                    .Span.Slice(offset, chunkLength)
                    .CopyTo(rented.AsSpan(Http2FrameCodec.FrameHeaderSize));
                await connection.SendAsync(
                    new ReadOnlySequence<byte>(rented.AsMemory(0, frameSize)),
                    ct
                );
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            offset += chunkLength;
            isFirst = false;
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
