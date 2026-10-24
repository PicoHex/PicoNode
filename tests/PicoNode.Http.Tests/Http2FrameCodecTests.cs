using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    [Test]
    public async Task WritePing_zeroes_the_payload_bytes_the_caller_did_not_supply()
    {
        // The destination is usually a rented buffer, so the octets a short opaqueData
        // does not cover must be cleared rather than published as whatever that array
        // held before (the plan's follow-up item folded into this task).
        var destination = new byte[Http2FrameCodec.FrameHeaderSize + 8];
        Array.Fill(destination, (byte)0xCC);
        var written = Http2FrameCodec.WritePing(destination, new byte[] { 1, 2, 3, 4 });

        await Assert.That(written).IsEqualTo(Http2FrameCodec.FrameHeaderSize + 8);
        await Assert
            .That(destination.AsSpan(Http2FrameCodec.FrameHeaderSize, 8).ToArray())
            .IsEquivalentTo(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 });

        // A shorter-than-8 caller keeps the same guarantee.
        Array.Fill(destination, (byte)0xCC);
        Http2FrameCodec.WritePing(destination, ReadOnlySpan<byte>.Empty);
        await Assert
            .That(destination.AsSpan(Http2FrameCodec.FrameHeaderSize, 8).ToArray())
            .IsEquivalentTo(new byte[8]);

        // And an over-long caller still writes exactly 8 octets.
        Array.Fill(destination, (byte)0xCC);
        Http2FrameCodec.WritePing(destination, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        await Assert
            .That(destination.AsSpan(Http2FrameCodec.FrameHeaderSize, 8).ToArray())
            .IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }

    // ── Zero-copy payload guards (audit F-25) ─────────────────────────────
    //
    // TryReadFrame used to copy every frame payload out of the receive buffer into
    // a fresh `new byte[length]` (or a pooled array above 256 B). Payloads are now
    // handed out as a view of the caller's buffer when they are contiguous, so the
    // only allocation left per frame is the Http2Frame object itself.

    private const int MeasuredRounds = 5;
    private const int WarmupParses = 32;

    /// <summary>
    /// Measured ceiling for the small-frame test, in bytes of garbage per 4-byte
    /// WINDOW_UPDATE frame. Derived from the measurement, not from the plan's estimate:
    /// after the change it measures exactly 56.0 B (the Http2Frame object and nothing
    /// else), which leaves this bound 28.6 % headroom. Before the change it measures
    /// 88.0 B (that object plus one 24 B byte[] per frame), so the old code fails it.
    /// The same bound is used for the pooled-copy path (a 1024-byte payload split over
    /// 700-byte segments), which measures the same 56.0 B: the copy is recycled through
    /// ArrayPool, so it is memcpy rather than garbage — unless a frame stops returning
    /// its buffer, which makes that test allocate a fresh ~1 KB array per frame.
    /// </summary>
    private const double SmallFrameCeilingBytes = 72.0;

    /// <summary>
    /// Measured ceiling for one call to <c>Http2StreamState.AppendHeaderData</c> when
    /// the whole HPACK block arrives in one HEADERS frame: after the change the block
    /// is returned as a view, so the measured value is exactly 0 B. The bound is set
    /// below the 24-byte object header of the smallest array a copy could possibly
    /// allocate, so any reintroduced copy fails it whatever the block size (the
    /// pre-change value is 424 B for a 400-byte block).
    /// </summary>
    private const double HeaderBlockCeilingBytes = 16.0;

    [Test]
    public async Task Read_frame_does_not_copy_small_payloads()
    {
        // 100 frames of 4-byte payloads back to back in ONE buffer, exactly as the
        // connection loop parses them.
        const int framesPerRound = 100;
        const int payloadLength = 4;
        var buffer = new ReadOnlySequence<byte>(
            BuildFrameStream(Http2FrameType.WindowUpdate, payloadLength, framesPerRound)
        );

        // Warm-up outside the window: JIT, and the first touch of anything static.
        for (var i = 0; i < WarmupParses; i++)
            AssertParsed(ParseFrameStream(buffer), framesPerRound, framesPerRound * payloadLength);

        var allocated = 0L;
        for (var round = 0; round < MeasuredRounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var parsed = ParseFrameStream(buffer);
            var after = GC.GetAllocatedBytesForCurrentThread();

            // The region must actually parse every frame, otherwise a "cheap" run
            // that parsed nothing would look like a win.
            AssertParsed(parsed, framesPerRound, framesPerRound * payloadLength);
            allocated += after - before;
        }

        var perFrame = (double)allocated / (MeasuredRounds * framesPerRound);
        await Assert
            .That(perFrame)
            .IsLessThan(SmallFrameCeilingBytes)
            .Because(
                $"measured {perFrame:N1} B of garbage per 4-byte WINDOW_UPDATE frame "
                    + $"({allocated:N0} B over {MeasuredRounds * framesPerRound} frames)"
            );
    }

    [Test]
    public async Task Read_frame_payload_is_a_view_of_the_input_buffer()
    {
        // A contiguous payload must not be copied — the frame's payload has to be the
        // receive buffer's memory. Asserted structurally (array identity), because a
        // pooled copy is invisible to GC.GetAllocatedBytesForCurrentThread; the 4 KB
        // payload is above the old 256 B pooling threshold, so a regression here is
        // exactly the "rent + copy" path this task removes.
        foreach (var payloadLength in new[] { 4, 4096 })
        {
            var input = BuildFrameStream(Http2FrameType.Data, payloadLength, 1);
            var buffer = new ReadOnlySequence<byte>(input);

            await Assert
                .That(Http2FrameCodec.TryReadFrame(buffer, out var frame, out var consumed))
                .IsTrue();
            await Assert.That(consumed).IsEqualTo(input.Length);

            var aliases =
                MemoryMarshal.TryGetArray(frame!.Payload, out var segment)
                && ReferenceEquals(segment.Array, input)
                && segment.Offset == Http2FrameCodec.FrameHeaderSize;
            await Assert
                .That(aliases)
                .IsTrue()
                .Because(
                    $"a {payloadLength}-byte contiguous payload must be a view of the receive "
                        + "buffer, not a copy the codec made"
                );
            await Assert.That(frame.Payload.Length).IsEqualTo(payloadLength);
        }
    }

    [Test]
    public async Task Read_frame_payloads_of_two_frames_in_one_buffer_do_not_alias_each_other()
    {
        // Use-after-return guard, part 1: two frames in ONE pooled array (what a pipe
        // segment is), parsed one after the other without the first being released.
        // Each payload must read back its own frame's bytes — a codec that rented or
        // shared a scratch buffer between frames would show frame 2's bytes in frame 1,
        // which is the corruption this change could introduce.
        const int payloadLength = 8;
        var frameSize = Http2FrameCodec.FrameHeaderSize + payloadLength;
        var array = ArrayPool<byte>.Shared.Rent(2 * frameSize);
        try
        {
            WriteFrame(array.AsSpan(0, frameSize), streamId: 1, fill: 0x11, payloadLength);
            WriteFrame(array.AsSpan(frameSize, frameSize), streamId: 3, fill: 0x22, payloadLength);

            var remaining = new ReadOnlySequence<byte>(array, 0, 2 * frameSize);
            await Assert.That(Http2FrameCodec.TryReadFrame(remaining, out var first, out _)).IsTrue();
            remaining = remaining.Slice(frameSize);
            await Assert
                .That(Http2FrameCodec.TryReadFrame(remaining, out var second, out _))
                .IsTrue();

            await Assert.That(first!.StreamId).IsEqualTo(1);
            await Assert.That(second!.StreamId).IsEqualTo(3);
            await Assert.That(AllBytesAre(first!.Payload.Span, 0x11)).IsTrue();
            await Assert.That(AllBytesAre(second!.Payload.Span, 0x22)).IsTrue();

            // Parsing the second frame must not have written anything into the buffer:
            // the codec reads its input, it never mutates it.
            await Assert
                .That(
                    AllBytesAre(
                        array.AsSpan(Http2FrameCodec.FrameHeaderSize, payloadLength),
                        0x11
                    )
                )
                .IsTrue();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    [Test]
    public async Task Read_frame_rents_one_buffer_per_frame_when_the_payload_spans_segments()
    {
        // Use-after-return guard, part 2: a payload split across pipe segments cannot be
        // a ReadOnlyMemory view, so the codec copies it into ONE pooled buffer it owns
        // and releases in ReturnPayload. Two such frames must not share that buffer, and
        // re-renting a released buffer must not corrupt the next frame.
        const int payloadLength = 1024;
        var frameSize = Http2FrameCodec.FrameHeaderSize + payloadLength;
        var array = new byte[2 * frameSize];
        WriteFrame(array.AsSpan(0, frameSize), streamId: 1, fill: 0x33, payloadLength);
        WriteFrame(array.AsSpan(frameSize, frameSize), streamId: 3, fill: 0x44, payloadLength);

        // Split every frame into 3 segments at offsets that put the payload across a seam
        // (segment 1 ends 4 bytes into the frame header of frame 1).
        var split = SplitInto3(array);
        await Assert.That(Http2FrameCodec.TryReadFrame(split, out var first, out var consumed)).IsTrue();
        await Assert
            .That(Http2FrameCodec.TryReadFrame(split.Slice(consumed), out var second, out _))
            .IsTrue();

        try
        {
            await Assert.That(first!.Payload.Length).IsEqualTo(payloadLength);
            await Assert.That(AllBytesAre(first!.Payload.Span, 0x33)).IsTrue();
            await Assert.That(AllBytesAre(second!.Payload.Span, 0x44)).IsTrue();
            // Two live frames must hold two distinct buffers: a shared one would make
            // the second parse overwrite the first frame's payload.
            _ = MemoryMarshal.TryGetArray(first!.Payload, out var firstSegment);
            _ = MemoryMarshal.TryGetArray(second!.Payload, out var secondSegment);
            await Assert
                .That(ReferenceEquals(firstSegment.Array, secondSegment.Array))
                .IsFalse()
                .Because("a pooled buffer must belong to exactly one frame");
        }
        finally
        {
            first!.ReturnPayload();
            second!.ReturnPayload();
        }

        // A buffer handed back to the pool is re-rented and fully rewritten: the same
        // parse again must produce the same bytes, not leftovers from the frames above.
        await Assert
            .That(Http2FrameCodec.TryReadFrame(split, out var again, out _))
            .IsTrue();
        await Assert.That(AllBytesAre(again!.Payload.Span, 0x33)).IsTrue();
        again.ReturnPayload();
    }

    [Test]
    public async Task Read_frame_returns_the_pooled_copy_so_the_next_frame_reuses_it()
    {
        // A split payload is copied into ONE pooled buffer per frame. Parsing such frames
        // back to back therefore has to reuse the pool: a payload buffer that the frame
        // never hands back makes every Rent allocate a fresh array (~1 KB per frame
        // here), which is exactly the regression this test pins.
        const int payloadLength = 1024;
        const int framesPerRound = 100;
        var bytes = BuildFrameStream(Http2FrameType.Data, payloadLength, framesPerRound, 0x66);
        // Segments of 700 bytes: every 1033-byte frame crosses a seam, so every frame
        // takes the pooled-copy path rather than the contiguous view.
        var buffer = SplitEvery(bytes, 700);

        for (var i = 0; i < WarmupParses; i++)
            AssertParsed(ParseFrameStream(buffer), framesPerRound, framesPerRound * payloadLength);

        var allocated = 0L;
        for (var round = 0; round < MeasuredRounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var parsed = ParseFrameStream(buffer);
            var after = GC.GetAllocatedBytesForCurrentThread();

            AssertParsed(parsed, framesPerRound, framesPerRound * payloadLength);
            allocated += after - before;
        }

        var perFrame = (double)allocated / (MeasuredRounds * framesPerRound);
        await Assert
            .That(perFrame)
            .IsLessThan(SmallFrameCeilingBytes)
            .Because(
                $"measured {perFrame:N1} B of garbage per {payloadLength}-byte SPLIT frame "
                    + $"({allocated:N0} B over {MeasuredRounds * framesPerRound} frames)"
            );
    }

    [Test]
    public async Task AppendHeaderData_does_not_copy_a_single_frame_header_block()
    {
        // The HPACK block of one HEADERS frame used to be copied out with ToArray()
        // before it was decoded; it is now handed back as a view of the frame payload.
        const int blockLength = 400;
        const int calls = 100;
        var block = new byte[blockLength];
        Array.Fill(block, (byte)0x82);
        var payload = new ReadOnlyMemory<byte>(block);
        var state = new Http2StreamState(1);

        // Warm-up outside the window.
        for (var i = 0; i < WarmupParses; i++)
        {
            ReadOnlyMemory<byte> warm = state.AppendHeaderData(payload, endHeaders: true)!.Value;
            if (warm.Length != blockLength)
                throw new InvalidOperationException("AppendHeaderData returned the wrong length");
        }

        var allocated = 0L;
        for (var round = 0; round < MeasuredRounds; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < calls; i++)
            {
                // Typed as ReadOnlyMemory so the guard compiles against both the old
                // ArraySegment<byte> result and the view this task introduces.
                ReadOnlyMemory<byte> result = state.AppendHeaderData(payload, endHeaders: true)!.Value;
                if (result.Length != blockLength)
                    throw new InvalidOperationException("AppendHeaderData returned the wrong length");
            }
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var perCall = (double)allocated / (MeasuredRounds * calls);
        await Assert
            .That(perCall)
            .IsLessThan(HeaderBlockCeilingBytes)
            .Because($"measured {perCall:N1} B of garbage per {blockLength}-byte header block");
    }

    [Test]
    public async Task AppendHeaderData_returns_a_view_of_the_single_frame_payload()
    {
        var block = new byte[] { 0x82, 0x86, 0x84 };
        var frameMemory = new ReadOnlyMemory<byte>(block);
        var state = new Http2StreamState(1);

        var result = state.AppendHeaderData(frameMemory, endHeaders: true);

        await Assert.That(result).IsNotNull();
        ReadOnlyMemory<byte> view = result!.Value;
        await Assert.That(view.Length).IsEqualTo(block.Length);
        var aliases =
            MemoryMarshal.TryGetArray(view, out var segment)
            && ReferenceEquals(segment.Array, block);
        await Assert
            .That(aliases)
            .IsTrue()
            .Because("the header block of a single HEADERS frame must not be copied");
        await Assert.That(state.HeaderBlockBuffer).IsNull();
    }

    private static void AssertParsed(
        (int Frames, long PayloadBytes) parsed,
        int expectedFrames,
        long expectedPayloadBytes
    )
    {
        if (parsed.Frames != expectedFrames || parsed.PayloadBytes != expectedPayloadBytes)
        {
            throw new InvalidOperationException(
                $"the measured region parsed {parsed.Frames} frames / {parsed.PayloadBytes} payload "
                    + $"bytes, expected {expectedFrames} / {expectedPayloadBytes}"
            );
        }
    }

    /// <summary>
    /// Parses every frame of a stream and sums the payload lengths. NoInlining and the
    /// returned sum keep the parse observable, so it cannot be optimized away.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int Frames, long PayloadBytes) ParseFrameStream(ReadOnlySequence<byte> buffer)
    {
        var frames = 0;
        var payloadBytes = 0L;
        var remaining = buffer;
        while (Http2FrameCodec.TryReadFrame(remaining, out var frame, out var consumed))
        {
            frames++;
            payloadBytes += frame!.Payload.Length;
            // Release the payload exactly as the connection loop does, so a pooled copy
            // is measured as a rent/return cycle rather than as a leak.
            frame.ReturnPayload();
            remaining = remaining.Slice(consumed);
        }
        return (frames, payloadBytes);
    }

    private static byte[] BuildFrameStream(
        Http2FrameType type,
        int payloadLength,
        int count,
        byte fill = 0x5A
    )
    {
        var payload = new byte[payloadLength];
        Array.Fill(payload, fill);
        var frame = Http2FrameCodec.EncodeFrame(type, Http2FrameFlags.None, 1, payload);
        var stream = new byte[frame.Length * count];
        for (var i = 0; i < count; i++)
            frame.CopyTo(stream, i * frame.Length);
        return stream;
    }

    private static void WriteFrame(
        Span<byte> destination,
        int streamId,
        byte fill,
        int payloadLength
    )
    {
        destination.Fill(fill);
        Http2FrameCodec.WriteFrameHeader(
            destination,
            payloadLength,
            Http2FrameType.Data,
            Http2FrameFlags.None,
            streamId
        );
    }

    private static bool AllBytesAre(ReadOnlySpan<byte> bytes, byte value)
    {
        foreach (var b in bytes)
        {
            if (b != value)
                return false;
        }
        return bytes.Length > 0;
    }

    /// <summary>Splits <paramref name="data"/> into equal <paramref name="chunk"/>-sized
    /// segments, so a frame larger than one chunk spans at least two of them.</summary>
    private static ReadOnlySequence<byte> SplitEvery(byte[] data, int chunk)
    {
        SequenceSegment? head = null;
        SequenceSegment? tail = null;
        for (var offset = 0; offset < data.Length; offset += chunk)
        {
            var length = Math.Min(chunk, data.Length - offset);
            if (head is null)
            {
                head = new SequenceSegment(data.AsMemory(offset, length));
                tail = head;
            }
            else
            {
                tail = tail!.Append(data.AsMemory(offset, length));
            }
        }

        return new ReadOnlySequence<byte>(head!, 0, tail!, tail!.Memory.Length);
    }

    /// <summary>Splits <paramref name="data"/> into three segments at offsets that put
    /// a frame header and a payload across seams.</summary>
    private static ReadOnlySequence<byte> SplitInto3(byte[] data)
    {
        var first = new SequenceSegment(data.AsMemory(0, 13));
        var second = first.Append(data.AsMemory(13, data.Length / 2));
        second.Append(data.AsMemory(13 + data.Length / 2));
        return new ReadOnlySequence<byte>(first, 0, second.Next!, second.Next!.Memory.Length);
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new SequenceSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
