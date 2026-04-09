namespace PicoNode.Http;

public sealed class Http2Frame
{
    public int Length { get; init; }

    public Http2FrameType Type { get; init; }

    public Http2FrameFlags Flags { get; init; }

    public int StreamId { get; init; }

    public ReadOnlyMemory<byte> Payload { get; init; }

    public bool HasFlag(Http2FrameFlags flag) => (Flags & flag) == flag;
}
