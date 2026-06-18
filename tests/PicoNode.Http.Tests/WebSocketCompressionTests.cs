namespace PicoNode.Http.Tests;

/// <summary>
/// Tests for WebSocket permessage-deflate compression pooling.
/// Verifies round-trip compress/decompress still works after MemoryStream reuse.
/// </summary>
public sealed class WebSocketCompressionTests
{
    private static bool BytesEqual(byte[] a, byte[] b) =>
        a.Length == b.Length && a.AsSpan().SequenceEqual(b);

    [Test]
    public async Task CompressDecompress_RoundTrip_SmallMessage()
    {
        var state = new WebSocketMessageProcessorState { CompressionNegotiated = true };
        var original = "Hello, WebSocket!"u8.ToArray();

        var compressed = state.Compress(original);
        var decompressed = state.Decompress(compressed);

        await Assert.That(BytesEqual(decompressed, original)).IsTrue();
    }

    [Test]
    public async Task CompressDecompress_RoundTrip_LargeMessage()
    {
        var state = new WebSocketMessageProcessorState { CompressionNegotiated = true };
        var original = new byte[10000];
        new Random(42).NextBytes(original);

        var compressed = state.Compress(original);
        var decompressed = state.Decompress(compressed);

        await Assert.That(BytesEqual(decompressed, original)).IsTrue();
    }

    [Test]
    public async Task CompressDecompress_MultipleMessages_ReusesStream()
    {
        var state = new WebSocketMessageProcessorState { CompressionNegotiated = true };

        for (int i = 0; i < 5; i++)
        {
            var original = Encoding.UTF8.GetBytes(
                $"Message number {i} with enough content to compress."
            );
            var compressed = state.Compress(original);
            var decompressed = state.Decompress(compressed);

            await Assert.That(BytesEqual(decompressed, original)).IsTrue();
        }
    }

    [Test]
    public async Task CompressDecompress_WithoutCompression_Passthrough()
    {
        var state = new WebSocketMessageProcessorState { CompressionNegotiated = false };
        var original = "no compression"u8.ToArray();

        var compressed = state.Compress(original);
        var decompressed = state.Decompress(compressed);

        await Assert.That(BytesEqual(decompressed, original)).IsTrue();
    }
}
