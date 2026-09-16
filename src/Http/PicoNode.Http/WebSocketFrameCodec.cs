namespace PicoNode.Http;

public static class WebSocketFrameCodec
{
    public static bool TryReadFrame(
        ReadOnlySequence<byte> buffer,
        out WebSocketFrame? frame,
        out long consumed,
        int maxPayloadLength = int.MaxValue
    )
    {
        frame = null;
        consumed = 0;

        if (!TryReadFrameHeader(buffer, out var header, out var tooLarge, maxPayloadLength))
        {
            // Public contract: -1 means "too large / invalid length" (not merely
            // incomplete). Rejecting before the array allocation keeps a malicious
            // 127-form length with the sign bit set from overflowing new byte[].
            if (tooLarge)
                consumed = -1;
            return false;
        }

        var payload = MaterializePayload(header);

        frame = new WebSocketFrame
        {
            Fin = header.Fin,
            Rsv1 = header.Rsv1,
            Rsv2 = header.Rsv2,
            Rsv3 = header.Rsv3,
            Masked = header.Masked,
            OpCode = header.OpCode,
            Payload = payload,
        };

        consumed = header.FrameLength;
        return true;
    }

    /// <summary>
    /// Parsed frame metadata without a materialised payload: the payload is a
    /// slice of the input sequence. Control-frame consumers can use the public
    /// <see cref="TryReadFrame"/>; the message processor streams data payloads
    /// into its reassembly buffer via <see cref="AppendPayload"/> to avoid a
    /// per-frame array (data frames can be MaxMessageSize-sized, i.e. LOH).
    /// </summary>
    internal readonly struct WebSocketFrameHeader
    {
        public bool Fin { get; init; }
        public bool Rsv1 { get; init; }
        public bool Rsv2 { get; init; }
        public bool Rsv3 { get; init; }
        public bool Masked { get; init; }
        public WebSocketOpCode OpCode { get; init; }
        public uint MaskKey { get; init; }
        public ReadOnlySequence<byte> Payload { get; init; }
        public long FrameLength { get; init; }
    }

    internal static bool TryReadFrameHeader(
        ReadOnlySequence<byte> buffer,
        out WebSocketFrameHeader header,
        out bool tooLarge,
        int maxPayloadLength = int.MaxValue
    )
    {
        header = default;
        tooLarge = false;

        if (buffer.Length < 2)
            return false;

        var reader = new SequenceReader<byte>(buffer);
        reader.TryRead(out var b0);
        reader.TryRead(out var b1);

        var payloadLength = (long)(b1 & 0x7F);
        if (payloadLength == 126)
        {
            if (reader.Remaining < 2)
                return false;

            reader.TryRead(out var h);
            reader.TryRead(out var l);
            payloadLength = (h << 8) | l;
        }
        else if (payloadLength == 127)
        {
            if (reader.Remaining < 8)
                return false;

            payloadLength = 0;
            for (var i = 0; i < 8; i++)
            {
                reader.TryRead(out var b);
                payloadLength = (payloadLength << 8) | b;
            }
        }

        uint maskKey = 0;
        if ((b1 & 0x80) != 0)
        {
            if (reader.Remaining < 4)
                return false;

            reader.TryRead(out var key0);
            reader.TryRead(out var key1);
            reader.TryRead(out var key2);
            reader.TryRead(out var key3);
            maskKey = (uint)(key0 | (key1 << 8) | (key2 << 16) | (key3 << 24));
        }

        // Reject BEFORE slicing: a malicious 127-form length with the sign bit
        // set arrives here negative.
        if (payloadLength > maxPayloadLength || payloadLength < 0)
        {
            tooLarge = true;
            return false;
        }

        if (reader.Remaining < payloadLength)
            return false;

        header = new WebSocketFrameHeader
        {
            Fin = (b0 & 0x80) != 0,
            Rsv1 = (b0 & 0x40) != 0,
            Rsv2 = (b0 & 0x20) != 0,
            Rsv3 = (b0 & 0x10) != 0,
            Masked = (b1 & 0x80) != 0,
            OpCode = (WebSocketOpCode)(b0 & 0x0F),
            MaskKey = maskKey,
            Payload = buffer.Slice(reader.Consumed, payloadLength),
            FrameLength = reader.Consumed + payloadLength,
        };
        return true;
    }

    /// <summary>
    /// Appends a frame payload slice to <paramref name="writer"/>, unmasking in
    /// place when the frame was masked — one copy, no intermediate array.
    /// </summary>
    internal static void AppendPayload(
        IBufferWriter<byte> writer,
        ReadOnlySequence<byte> payload,
        bool masked,
        uint maskKey
    )
    {
        var length = (int)payload.Length;
        if (length == 0)
            return;

        var span = writer.GetSpan(length);
        payload.CopyTo(span);

        if (masked)
        {
            for (var i = 0; i < length; i++)
            {
                span[i] ^= (byte)(maskKey >> (8 * (i & 3)));
            }
        }

        writer.Advance(length);
    }

    /// <summary>
    /// Copies a frame payload into a new array, unmasking it when the frame
    /// was masked. Control-frame consumers (Ping echo, Close reason) use this;
    /// data frames stream through <see cref="AppendPayload"/> instead.
    /// </summary>
    internal static byte[] MaterializePayload(in WebSocketFrameHeader header)
    {
        if (header.Payload.Length == 0)
            return [];

        var payload = new byte[header.Payload.Length];
        header.Payload.CopyTo(payload);

        if (header.Masked)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= (byte)(header.MaskKey >> (8 * (i & 3)));
            }
        }

        return payload;
    }

    public static int MeasureFrameSize(int payloadLength, bool mask = false)
    {
        var headerSize = 2;
        if (payloadLength is >= 126 and <= 65535)
            headerSize += 2;
        else if (payloadLength > 65535)
            headerSize += 8;
        if (mask)
            headerSize += 4;
        return headerSize + payloadLength;
    }

    public static int WriteFrame(
        Span<byte> destination,
        WebSocketOpCode opCode,
        ReadOnlySpan<byte> payload,
        bool mask = false,
        bool rsv1 = false
    )
    {
        var pos = 0;

        var b0 = 0x80 | (byte)opCode;
        if (rsv1)
            b0 |= 0x40;
        destination[pos++] = (byte)b0;

        switch (payload.Length)
        {
            case < 126:
                destination[pos++] = (byte)((mask ? 0x80 : 0) | payload.Length);
                break;
            case <= 65535:
                destination[pos++] = (byte)((mask ? 0x80 : 0) | 126);
                destination[pos++] = (byte)(payload.Length >> 8);
                destination[pos++] = (byte)(payload.Length & 0xFF);
                break;
            default:
            {
                destination[pos++] = (byte)((mask ? 0x80 : 0) | 127);
                var len = (long)payload.Length;
                for (var i = 7; i >= 0; i--)
                    destination[pos++] = (byte)((len >> (i * 8)) & 0xFF);
                break;
            }
        }

        if (mask)
        {
            Span<byte> maskKey = stackalloc byte[4];
            Random.Shared.NextBytes(maskKey);
            maskKey.CopyTo(destination.Slice(pos));
            pos += 4;
        }

        payload.CopyTo(destination.Slice(pos));

        if (mask)
        {
            var maskStart = pos - 4;
            for (var i = 0; i < payload.Length; i++)
                destination[pos + i] ^= destination[maskStart + (i % 4)];
        }

        pos += payload.Length;
        return pos;
    }

    public static byte[] EncodeFrame(
        WebSocketOpCode opCode,
        ReadOnlySpan<byte> payload,
        bool mask = false,
        bool rsv1 = false
    )
    {
        var result = new byte[MeasureFrameSize(payload.Length, mask)];
        WriteFrame(result, opCode, payload, mask, rsv1);
        return result;
    }
}
