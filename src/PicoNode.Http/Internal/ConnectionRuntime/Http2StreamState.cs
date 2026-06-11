namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class Http2StreamState
{
    /// <summary>Default max header list size: 16 KB (same as typical HTTP/2 default).</summary>
    internal const int MaxHeaderListSize = 16 * 1024;

    public int StreamId { get; }
    public bool EndHeadersReceived { get; set; }
    public bool EndStreamReceived { get; set; }
    public ArrayBufferWriter<byte>? HeaderBlockBuffer { get; set; }
    public bool ResponseSent { get; set; }

    public Http2StreamState(int streamId)
    {
        StreamId = streamId;
    }

    /// <summary>
    /// Combines buffered CONTINUATION data with the current frame payload.
    /// Returns null when headers are still incomplete (expecting CONTINUATION).
    /// Returns non-null when END_HEADERS is received, containing the complete header block.
    /// Throws <see cref="Http2HeaderTooLargeException"/> if the total exceeds <see cref="MaxHeaderListSize"/>.
    /// </summary>
    public ArraySegment<byte>? AppendHeaderData(ReadOnlyMemory<byte> payload, bool endHeaders)
    {
        if (endHeaders)
        {
            if (HeaderBlockBuffer is null)
            {
                // No prior CONTINUATION; return the frame payload directly.
                if (payload.Length == 0)
                    return null;
                if (payload.Length > MaxHeaderListSize)
                    throw new Http2HeaderTooLargeException(payload.Length);
                return new ArraySegment<byte>(payload.ToArray());
            }

            // Flush buffered data + final payload.
            HeaderBlockBuffer.Write(payload.Span);
            if (HeaderBlockBuffer.WrittenCount > MaxHeaderListSize)
                throw new Http2HeaderTooLargeException(HeaderBlockBuffer.WrittenCount);
            EndHeadersReceived = true;
            var result = HeaderBlockBuffer.WrittenMemory.ToArray();
            HeaderBlockBuffer = null;
            return result;
        }
        else
        {
            // Buffer for future CONTINUATION.
            HeaderBlockBuffer ??= new ArrayBufferWriter<byte>();
            HeaderBlockBuffer.Write(payload.Span);
            if (HeaderBlockBuffer.WrittenCount > MaxHeaderListSize)
                throw new Http2HeaderTooLargeException(HeaderBlockBuffer.WrittenCount);
            return null;
        }
    }
}

internal sealed class Http2HeaderTooLargeException : InvalidOperationException
{
    public int Size { get; }

    public Http2HeaderTooLargeException(int size)
        : base(
            $"HTTP/2 header list exceeds maximum size ({size} > {Http2StreamState.MaxHeaderListSize})"
        )
    {
        Size = size;
    }
}
