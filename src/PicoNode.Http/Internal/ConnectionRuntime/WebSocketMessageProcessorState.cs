using System.IO.Compression;

namespace PicoNode.Http.Internal.ConnectionRuntime;

internal sealed class WebSocketMessageProcessorState
{
    public WebSocketOpCode? MessageOpCode { get; set; }

    public ArrayBufferWriter<byte> PayloadBuffer { get; } = new();

    /// <summary>Whether permessage-deflate compression was negotiated.</summary>
    public bool CompressionNegotiated { get; set; }

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
}
