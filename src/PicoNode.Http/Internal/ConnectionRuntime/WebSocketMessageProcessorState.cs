using System.IO.Compression;

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
        IdleTimeout > TimeSpan.Zero
        && DateTime.UtcNow - LastFrameReceivedAt >= IdleTimeout;

    /// <summary>Compresses outgoing message payload using deflate.</summary>
    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (!CompressionNegotiated || data.Length == 0)
            return data.ToArray();

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return output.ToArray();
    }

    /// <summary>Decompresses incoming message payload using inflate.</summary>
    public byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (!CompressionNegotiated || data.Length == 0)
            return data.ToArray();

        using var input = new MemoryStream(data.ToArray());
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Compresses outgoing message payload using deflate. Caller should set RSV1 bit on the frame.</summary>
    public byte[] CompressOutgoing(ReadOnlySpan<byte> data)
    {
        if (!CompressionNegotiated || data.Length == 0)
            return data.ToArray();

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return output.ToArray();
    }
}
