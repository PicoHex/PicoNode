using System.Text;

namespace PicoNode.Http.Tests;

public sealed class Http2FrameCodecTests
{
    [Test]
    public async Task EncodeFrame_and_TryReadFrame_roundtrip_data_frame()
    {
        var payload = "Hello, HTTP/2!"u8.ToArray();
        var encoded = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Data,
            Http2FrameFlags.EndStream,
            1,
            payload
        );

        var buffer = new ReadOnlySequence<byte>(encoded);
        var success = Http2FrameCodec.TryReadFrame(buffer, out var frame, out var consumed);

        await Assert.That(success).IsTrue();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Type).IsEqualTo(Http2FrameType.Data);
        await Assert.That(frame.Flags).IsEqualTo(Http2FrameFlags.EndStream);
        await Assert.That(frame.StreamId).IsEqualTo(1);
        await Assert.That(frame.Length).IsEqualTo(payload.Length);
        await Assert.That(Encoding.UTF8.GetString(frame.Payload.Span)).IsEqualTo("Hello, HTTP/2!");
        await Assert.That(consumed).IsEqualTo(encoded.Length);
    }

    [Test]
    public async Task TryReadFrame_returns_false_for_incomplete_header()
    {
        var buffer = new ReadOnlySequence<byte>(new byte[5]);
        var success = Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsFalse();
        await Assert.That(frame).IsNull();
    }

    [Test]
    public async Task TryReadFrame_returns_false_for_incomplete_payload()
    {
        var encoded = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Data,
            Http2FrameFlags.None,
            1,
            new byte[100]
        );
        var truncated = encoded.AsMemory(0, Http2FrameCodec.FrameHeaderSize + 50);
        var buffer = new ReadOnlySequence<byte>(truncated);
        var success = Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsFalse();
        await Assert.That(frame).IsNull();
    }

    [Test]
    public async Task TryReadFrame_rejects_frame_exceeding_max_size()
    {
        // Create a frame header claiming 32KB of payload
        var header = new byte[Http2FrameCodec.FrameHeaderSize + 32768];
        header[0] = 0x00;
        header[1] = 0x80;
        header[2] = 0x00; // length = 32768
        header[3] = 0x00; // type = DATA
        header[4] = 0x00; // flags
        // stream id = 0

        var buffer = new ReadOnlySequence<byte>(header);
        var success = Http2FrameCodec.TryReadFrame(
            buffer,
            out var frame,
            out _,
            maxFrameSize: 16384
        );

        await Assert.That(success).IsFalse();
    }

    [Test]
    public async Task EncodeSettings_creates_valid_settings_frame()
    {
        var encoded = Http2FrameCodec.EncodeSettings(
            new Http2Setting(Http2SettingId.MaxConcurrentStreams, 100),
            new Http2Setting(Http2SettingId.InitialWindowSize, 65535)
        );

        var buffer = new ReadOnlySequence<byte>(encoded);
        var success = Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(frame!.Type).IsEqualTo(Http2FrameType.Settings);
        await Assert.That(frame.StreamId).IsEqualTo(0);
        await Assert.That(frame.Length).IsEqualTo(12); // 2 settings * 6 bytes each
    }

    [Test]
    public async Task ParseSettings_reads_settings_correctly()
    {
        var encoded = Http2FrameCodec.EncodeSettings(
            new Http2Setting(Http2SettingId.MaxConcurrentStreams, 100),
            new Http2Setting(Http2SettingId.MaxFrameSize, 32768)
        );

        var buffer = new ReadOnlySequence<byte>(encoded);
        Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        var settings = Http2FrameCodec.ParseSettings(frame!.Payload.Span);

        await Assert.That(settings.Count).IsEqualTo(2);
        await Assert.That(settings[0].Id).IsEqualTo(Http2SettingId.MaxConcurrentStreams);
        await Assert.That(settings[0].Value).IsEqualTo(100u);
        await Assert.That(settings[1].Id).IsEqualTo(Http2SettingId.MaxFrameSize);
        await Assert.That(settings[1].Value).IsEqualTo(32768u);
    }

    [Test]
    public async Task EncodeSettingsAck_creates_empty_settings_with_ack_flag()
    {
        var encoded = Http2FrameCodec.EncodeSettingsAck();

        var buffer = new ReadOnlySequence<byte>(encoded);
        var success = Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(frame!.Type).IsEqualTo(Http2FrameType.Settings);
        await Assert.That(frame.HasFlag(Http2FrameFlags.Ack)).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EncodePing_roundtrips_correctly()
    {
        var opaqueData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var encoded = Http2FrameCodec.EncodePing(opaqueData);

        var buffer = new ReadOnlySequence<byte>(encoded);
        Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(frame!.Type).IsEqualTo(Http2FrameType.Ping);
        await Assert.That(frame.HasFlag(Http2FrameFlags.Ack)).IsFalse();
        await Assert.That(frame.Payload.Length).IsEqualTo(8);
        await Assert.That(frame.Payload.Span[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task EncodePing_ack_sets_flag()
    {
        var opaqueData = new byte[8];
        var encoded = Http2FrameCodec.EncodePing(opaqueData, ack: true);

        var buffer = new ReadOnlySequence<byte>(encoded);
        Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(frame!.HasFlag(Http2FrameFlags.Ack)).IsTrue();
    }

    [Test]
    public async Task EncodeGoAway_encodes_last_stream_and_error_code()
    {
        var encoded = Http2FrameCodec.EncodeGoAway(5, Http2ErrorCode.NoError);

        var buffer = new ReadOnlySequence<byte>(encoded);
        Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(frame!.Type).IsEqualTo(Http2FrameType.GoAway);
        await Assert.That(frame.StreamId).IsEqualTo(0);
        await Assert.That(frame.Length).IsEqualTo(8);

        // Last stream ID = 5
        var payloadArray = frame.Payload.ToArray();
        var lastStreamId =
            ((payloadArray[0] & 0x7F) << 24)
            | (payloadArray[1] << 16)
            | (payloadArray[2] << 8)
            | payloadArray[3];
        await Assert.That(lastStreamId).IsEqualTo(5);

        // Error code = 0 (NoError)
        var errorCode = (uint)(
            (payloadArray[4] << 24)
            | (payloadArray[5] << 16)
            | (payloadArray[6] << 8)
            | payloadArray[7]
        );
        await Assert.That(errorCode).IsEqualTo(0u);
    }

    [Test]
    public async Task IsConnectionPreface_detects_valid_preface()
    {
        var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

        await Assert.That(Http2FrameCodec.IsConnectionPreface(preface)).IsTrue();
    }

    [Test]
    public async Task IsConnectionPreface_rejects_invalid_data()
    {
        var invalid = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n");

        await Assert.That(Http2FrameCodec.IsConnectionPreface(invalid)).IsFalse();
    }

    [Test]
    public async Task HasFlag_checks_individual_flags()
    {
        var frame = new Http2Frame
        {
            Type = Http2FrameType.Headers,
            Flags = Http2FrameFlags.EndStream | Http2FrameFlags.EndHeaders,
            StreamId = 1,
            Payload = ReadOnlyMemory<byte>.Empty,
        };

        await Assert.That(frame.HasFlag(Http2FrameFlags.EndHeaders)).IsTrue();
        await Assert.That(frame.HasFlag(Http2FrameFlags.Padded)).IsFalse();
    }

    [Test]
    public async Task Headers_frame_roundtrip()
    {
        var headerBlock = new byte[] { 0x82, 0x86, 0x84 }; // Dummy HPACK data
        var encoded = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            3,
            headerBlock
        );

        var buffer = new ReadOnlySequence<byte>(encoded);
        Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(frame!.Type).IsEqualTo(Http2FrameType.Headers);
        await Assert.That(frame.StreamId).IsEqualTo(3);
        await Assert.That(frame.HasFlag(Http2FrameFlags.EndHeaders)).IsTrue();
        await Assert.That(frame.Payload.Length).IsEqualTo(3);
    }

    [Test]
    public async Task FrameHeaderSize_is_nine_bytes()
    {
        var headerSize = Http2FrameCodec.FrameHeaderSize;
        await Assert.That(headerSize).IsEqualTo(9);
    }
}
