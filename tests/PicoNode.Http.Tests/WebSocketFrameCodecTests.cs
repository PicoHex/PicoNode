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
}
