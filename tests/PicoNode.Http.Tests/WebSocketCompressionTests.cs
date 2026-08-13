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

    [Test]
    public async Task Decompress_OutputExceedingLimit_ThrowsInvalidData()
    {
        var state = new WebSocketMessageProcessorState { CompressionNegotiated = true };
        // Highly compressible 1 MiB payload inflates far beyond the limit.
        var compressed = state.Compress(new byte[1024 * 1024]);

        await Assert
            .That(() => state.Decompress(compressed, maxOutputSize: 1024))
            .Throws<InvalidDataException>()
            .Because("decompression must be bounded to prevent zip-bomb memory exhaustion");
    }

    [Test]
    public async Task Decompress_OutputWithinLimit_Succeeds()
    {
        var state = new WebSocketMessageProcessorState { CompressionNegotiated = true };
        var original = new byte[1000];
        new Random(7).NextBytes(original);
        var compressed = state.Compress(original);

        var decompressed = state.Decompress(compressed, maxOutputSize: 1024 * 1024);

        await Assert.That(BytesEqual(decompressed, original)).IsTrue();
    }

    [Test]
    public async Task Fragmented_compressed_message_round_trips()
    {
        var state = new WebSocketMessageProcessorState { CompressionNegotiated = true };
        var received = new List<byte[]>();
        var connection = new RecordingConnectionContext();

        // Compress the full message, then split the compressed bytes across two frames:
        // first frame: FIN=0, RSV1=1, part 1; continuation: FIN=1, RSV1=0, part 2.
        var message = Encoding.UTF8.GetBytes(new string('a', 5000));
        var compressed = state.Compress(message);
        var split = compressed.Length / 2;
        var frame1 = BuildMaskedFrame(
            WebSocketOpCode.Text,
            fin: false,
            rsv1: true,
            compressed[..split]
        );
        var frame2 = BuildMaskedFrame(
            WebSocketOpCode.Continuation,
            fin: true,
            rsv1: false,
            compressed[split..]
        );

        await WebSocketMessageProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame1),
            (msg, _, _) =>
            {
                received.Add(msg.Payload.ToArray());
                return ValueTask.CompletedTask;
            },
            CancellationToken.None,
            state
        );
        await WebSocketMessageProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame2),
            (msg, _, _) =>
            {
                received.Add(msg.Payload.ToArray());
                return ValueTask.CompletedTask;
            },
            CancellationToken.None,
            state
        );

        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(received[0])).IsEqualTo(new string('a', 5000));
    }

    /// <summary>Builds a client-to-server frame (masked) with the given opcode/flags.</summary>
    private static byte[] BuildMaskedFrame(
        WebSocketOpCode opCode,
        bool fin,
        bool rsv1,
        byte[] payload
    )
    {
        byte b0 = (byte)((fin ? 0x80 : 0) | (rsv1 ? 0x40 : 0) | (byte)opCode);
        var header = new List<byte> { b0 };
        if (payload.Length < 126)
            header.Add((byte)(0x80 | payload.Length));
        else if (payload.Length <= ushort.MaxValue)
            header.AddRange([0x80 | 126, (byte)(payload.Length >> 8), (byte)payload.Length]);
        else
            throw new NotSupportedException("test payload too large");
        byte[] mask = [0x11, 0x22, 0x33, 0x44];
        header.AddRange(mask);
        var masked = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i++)
            masked[i] = (byte)(payload[i] ^ mask[i % 4]);
        return [.. header, .. masked];
    }

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId => 1;

        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc => DateTimeOffset.UnixEpoch;

        public object? UserState { get; set; }

        public string? NegotiatedProtocol => null;

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public void Close() { }
    }
}
