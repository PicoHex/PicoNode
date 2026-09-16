using System.IO.Pipelines;
using PicoNode.Http;

namespace PicoNode.Http.Tests;

/// <summary>
/// Header-level frame parsing: the message processor streams data-frame
/// payloads straight into its reassembly buffer instead of materialising a
/// per-frame byte[] (data frames can reach MaxMessageSize, i.e. LOH-sized).
/// </summary>
public sealed class WebSocketFrameHeaderTests
{
    [Test]
    public async Task Header_exposes_unmasked_payload_slice()
    {
        var payload = "hello"u8.ToArray();
        var frame = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Text, payload, mask: true);

        var parsed = WebSocketFrameCodec.TryReadFrameHeader(
            new ReadOnlySequence<byte>(frame),
            out var header,
            out var tooLarge
        );

        await Assert.That(parsed).IsTrue();
        await Assert.That(tooLarge).IsFalse();
        await Assert.That(header.Fin).IsTrue();
        await Assert.That(header.OpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert.That(header.Masked).IsTrue();
        await Assert.That(header.FrameLength).IsEqualTo(frame.Length);

        var sink = new ArrayBufferWriter<byte>();
        WebSocketFrameCodec.AppendPayload(sink, header.Payload, header.Masked, header.MaskKey);

        await Assert.That(sink.WrittenSpan.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task AppendPayload_leaves_unmasked_payload_untouched()
    {
        var payload = "plain"u8.ToArray();
        var frame = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Binary, payload, mask: false);

        var parsed = WebSocketFrameCodec.TryReadFrameHeader(
            new ReadOnlySequence<byte>(frame),
            out var header,
            out _
        );

        await Assert.That(parsed).IsTrue();
        await Assert.That(header.Masked).IsFalse();

        var sink = new ArrayBufferWriter<byte>();
        WebSocketFrameCodec.AppendPayload(sink, header.Payload, header.Masked, header.MaskKey);

        await Assert.That(sink.WrittenSpan.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task Header_reports_oversized_payload()
    {
        var payload = new byte[64];
        var frame = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Binary, payload, mask: true);

        var parsed = WebSocketFrameCodec.TryReadFrameHeader(
            new ReadOnlySequence<byte>(frame),
            out _,
            out var tooLarge,
            maxPayloadLength: 16
        );

        await Assert.That(parsed).IsFalse();
        await Assert.That(tooLarge).IsTrue();
    }

    [Test]
    public async Task Header_waits_for_incomplete_frame()
    {
        var frame = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Text,
            "hello"u8.ToArray(),
            mask: true
        );

        var parsed = WebSocketFrameCodec.TryReadFrameHeader(
            new ReadOnlySequence<byte>(frame.AsMemory(0, 4)),
            out _,
            out var tooLarge
        );

        await Assert.That(parsed).IsFalse();
        await Assert.That(tooLarge).IsFalse();
    }

    [Test]
    public async Task Header_handles_multi_segment_payload()
    {
        var payload = "segmented"u8.ToArray();
        var frame = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Binary, payload, mask: true);

        // Split mid-frame so header, mask key, and payload cross segments.
        var first = new Segment(frame.AsMemory(0, 5));
        var second = first.Append(frame.AsMemory(5));
        var multi = new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);

        var parsed = WebSocketFrameCodec.TryReadFrameHeader(multi, out var header, out _);

        await Assert.That(parsed).IsTrue();

        var sink = new ArrayBufferWriter<byte>();
        WebSocketFrameCodec.AppendPayload(sink, header.Payload, header.Masked, header.MaskKey);

        await Assert.That(sink.WrittenSpan.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task MaterializePayload_unmasks_masked_frame()
    {
        var payload = "echo-me"u8.ToArray();
        var frame = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Ping, payload, mask: true);

        var parsed = WebSocketFrameCodec.TryReadFrameHeader(
            new ReadOnlySequence<byte>(frame),
            out var header,
            out _
        );

        await Assert.That(parsed).IsTrue();
        var materialized = WebSocketFrameCodec.MaterializePayload(header);
        await Assert.That(materialized.AsSpan().SequenceEqual(payload)).IsTrue();
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
