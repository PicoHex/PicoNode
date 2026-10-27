namespace PicoNode.Http;

/// <summary>
/// Why <see cref="WebSocketFrameCodec.TryReadFrameHeader"/> failed to produce a
/// header. <see cref="None"/> means the frame is merely incomplete (wait for
/// more bytes); every other value is a defect the peer must be closed for.
/// </summary>
internal enum WebSocketFrameHeaderError
{
    /// <summary>The frame is incomplete, or the header parsed cleanly.</summary>
    None = 0,

    /// <summary>
    /// RFC 6455 §5.2: the minimal number of bytes MUST be used to encode the
    /// payload length — the 126 form only for 126..65535, the 127 form only
    /// above that. A longer-than-needed form is a protocol error (close 1002).
    /// </summary>
    NonMinimalLength,

    /// <summary>
    /// The payload length exceeds the configured maximum (or the 127 form had
    /// its reserved most-significant bit set). RFC 6455 §7.4.1: close 1009.
    /// </summary>
    TooLarge,
}
