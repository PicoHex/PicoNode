namespace PicoNode.Http.Tests;

/// <summary>
/// The public codec is deliberately permissive about the mask bit: it decodes
/// whatever it is handed and reports the flag verbatim. Masking enforcement is
/// the connection layer's job (RFC 6455 §5.1) and is pinned by
/// <c>WebSocketMessageProcessorTests.Unmasked_client_frame_is_rejected_with_close_1002</c>.
/// </summary>
public sealed class WebSocketMaskingTests
{
    [Test]
    public async Task TryReadFrame_reads_unmasked_frame_and_exposes_the_flag()
    {
        // Build a text frame without the mask bit set.
        var payload = "Hello"u8.ToArray();
        var frame = new byte[2 + payload.Length];
        frame[0] = 0x81; // FIN=1, Text, no mask bit
        frame[1] = (byte)payload.Length; // MSB clear = unmasked, no mask key follows
        payload.CopyTo(frame.AsSpan(2));

        var buffer = new ReadOnlySequence<byte>(frame);
        var success = WebSocketFrameCodec.TryReadFrame(buffer, out var decoded, out var consumed);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded).IsNotNull();
        await Assert
            .That(decoded!.Masked)
            .IsFalse()
            .Because("the codec reports the mask bit, it does not enforce it");
        await Assert.That(decoded.OpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert.That(Encoding.UTF8.GetString(decoded.Payload.Span)).IsEqualTo("Hello");
        await Assert.That(consumed).IsEqualTo(frame.Length);
    }
}
