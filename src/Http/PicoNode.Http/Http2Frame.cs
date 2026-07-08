namespace PicoNode.Http;

public sealed class Http2Frame
{
    private const int PoolThreshold = 256;

    private byte[]? _rentedBuffer;

    public int Length { get; init; }

    public Http2FrameType Type { get; init; }

    public Http2FrameFlags Flags { get; init; }

    public int StreamId { get; init; }

    public ReadOnlyMemory<byte> Payload { get; init; }

    public bool HasFlag(Http2FrameFlags flag) => (Flags & flag) == flag;

    /// <summary>Initializes pooling for this frame. Called by Http2FrameCodec.</summary>
    internal void SetPooledBuffer(byte[] buffer)
    {
        _rentedBuffer = buffer;
    }

    /// <summary>Returns the pooled buffer to ArrayPool, if applicable.</summary>
    internal void ReturnPayload()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }

    /// <summary>Returns true when the frame payload is large enough to warrant pooling.</summary>
    internal static bool ShouldPool(int length) => length > PoolThreshold;
}
