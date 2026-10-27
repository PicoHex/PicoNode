namespace PicoNode.Http;

/// <summary>
/// One decoded HTTP/2 frame. The payload's memory is owned by somebody else:
/// either the receive buffer the frame was decoded from (a zero-copy view, valid until
/// the caller advances past the frame's bytes) or, when the payload spanned buffer
/// segments, one ArrayPool buffer this frame owns and returns in
/// <see cref="ReturnPayload"/>. Reading <see cref="Payload"/> after the caller consumed
/// the frame is use-after-return.
/// </summary>
public sealed class Http2Frame
{
    private byte[]? _rentedBuffer;

    public int Length { get; init; }

    public Http2FrameType Type { get; init; }

    public Http2FrameFlags Flags { get; init; }

    public int StreamId { get; init; }

    /// <summary>
    /// The frame payload. It is a view of the receive buffer when the payload is
    /// contiguous, and this frame's pooled copy when it is not - see the type's remarks
    /// for who owns it and for how long.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    public bool HasFlag(Http2FrameFlags flag) => (Flags & flag) == flag;

    /// <summary>Initializes pooling for this frame. Called by Http2FrameCodec.</summary>
    internal void SetPooledBuffer(byte[] buffer)
    {
        _rentedBuffer = buffer;
    }

    /// <summary>Returns the payload's pooled buffer to ArrayPool, if the payload was a
    /// copy (i.e. it spanned receive-buffer segments). A view has nothing to return.
    /// </summary>
    internal void ReturnPayload()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }
}
