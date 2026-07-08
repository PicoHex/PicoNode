namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class WebSocketMessageProcessorState
{
    public const int DefaultMaxMessageSize = 256 * 1024; // 256 KB

    public WebSocketOpCode? MessageOpCode { get; set; }

    public ArrayBufferWriter<byte> PayloadBuffer { get; } = new();

    public int MaxMessageSize { get; set; } = DefaultMaxMessageSize;

    /// <summary>Whether permessage-deflate compression was negotiated.</summary>
    public bool CompressionNegotiated { get; set; }

    /// <summary>UTC timestamp of the last received frame (for idle timeout).</summary>
    public DateTime LastFrameReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ping interval. Zero = no automatic ping.</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.Zero;

    /// <summary>Time since last frame before connection is closed as idle.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Returns true if the connection should be closed due to idle timeout.</summary>
    public bool IsIdleTimedOut =>
        IdleTimeout > TimeSpan.Zero && DateTime.UtcNow - LastFrameReceivedAt >= IdleTimeout;

    // Reusable MemoryStream instances to reduce per-message allocation
    private MemoryStream? _compressOutput;
    private MemoryStream? _decompressOutput;

    /// <summary>Compresses outgoing message payload using deflate.</summary>
    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (!CompressionNegotiated || data.Length == 0)
            return data.ToArray();

        _compressOutput ??= new MemoryStream();
        _compressOutput.SetLength(0);

        using (
            var deflate = new DeflateStream(
                _compressOutput,
                CompressionLevel.Fastest,
                leaveOpen: true
            )
        )
        {
            deflate.Write(data);
        }
        return _compressOutput.ToArray();
    }

    /// <summary>Decompresses incoming message payload using inflate.</summary>
    public byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (!CompressionNegotiated || data.Length == 0)
            return data.ToArray();

        using var input = new MemoryStream(data.ToArray());
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);

        _decompressOutput ??= new MemoryStream();
        _decompressOutput.SetLength(0);

        inflate.CopyTo(_decompressOutput);
        return _decompressOutput.ToArray();
    }

    /// <summary>Compresses outgoing message payload using deflate. Caller should set RSV1 bit on the frame.</summary>
    public byte[] CompressOutgoing(ReadOnlySpan<byte> data) => Compress(data);
}
