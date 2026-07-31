namespace PicoNode.Http.Tests;

public sealed class WebSocketFrameCodecTests
{
    [Test]
    public async Task WriteFrame_return_value_matches_MeasureFrameSize()
    {
        // Arrange: create a buffer larger than the frame needs
        var payload = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"
        var expectedSize = WebSocketFrameCodec.MeasureFrameSize(payload.Length, mask: false);
        var buffer = new byte[expectedSize + 16]; // larger than needed

        // Act
        var written = WebSocketFrameCodec.WriteFrame(buffer, WebSocketOpCode.Text, payload);

        // Assert
        // TDD RED: this SHOULD fail — bug returns destination.Length (expectedSize + 16)
        await Assert.That(written).IsEqualTo(expectedSize);
    }

    [Test]
    public async Task WriteFrame_returns_correct_size_for_masked_frame()
    {
        // Arrange
        var payload = "Hello World"u8.ToArray();
        var expectedSize = WebSocketFrameCodec.MeasureFrameSize(payload.Length, mask: true);
        var buffer = new byte[expectedSize + 32]; // larger than needed

        // Act
        var written = WebSocketFrameCodec.WriteFrame(
            buffer,
            WebSocketOpCode.Text,
            payload,
            mask: true
        );

        // Assert
        // TDD RED: this SHOULD fail — bug returns destination.Length (expectedSize + 32)
        await Assert.That(written).IsEqualTo(expectedSize);
    }

    [Test]
    public async Task WriteFrame_returns_correct_size_for_empty_payload()
    {
        // Arrange
        var expectedSize = WebSocketFrameCodec.MeasureFrameSize(0, mask: false);
        var buffer = new byte[expectedSize + 10]; // larger than needed

        // Act
        var written = WebSocketFrameCodec.WriteFrame(buffer, WebSocketOpCode.Close, []);

        // Assert
        // TDD RED: this SHOULD fail — bug returns destination.Length (expectedSize + 10)
        await Assert.That(written).IsEqualTo(expectedSize);
    }

    [Test]
    public async Task TryReadFrame_rejects_payload_exceeding_max_length()
    {
        // Frame header declaring length 4096 (126-form) — payload not required:
        // the cap must reject on header alone.
        byte[] header = [0x81, 0x7E, 0x10, 0x00]; // FIN+Text, 126-form length 4096
        var buffer = new ReadOnlySequence<byte>(header);

        var ok = WebSocketFrameCodec.TryReadFrame(
            buffer,
            out _,
            out var consumed,
            maxPayloadLength: 1024
        );

        await Assert.That(ok).IsFalse();
        await Assert.That(consumed).IsEqualTo(-1);
    }

    [Test]
    public async Task TryReadFrame_negative_length_encoding_is_rejected()
    {
        // 127-form length with the high bit set — must be rejected before
        // any payload allocation (a naive new byte[length] would crash).
        byte[] header = [0x81, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F];
        var buffer = new ReadOnlySequence<byte>(header);

        var ok = WebSocketFrameCodec.TryReadFrame(buffer, out _, out var consumed);

        await Assert.That(ok).IsFalse();
        await Assert.That(consumed).IsEqualTo(-1);
    }
}
