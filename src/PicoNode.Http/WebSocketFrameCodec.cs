namespace PicoNode.Http;

public static class WebSocketFrameCodec
{
    public static bool TryReadFrame(
        ReadOnlySequence<byte> buffer,
        out WebSocketFrame? frame,
        out long consumed
    )
    {
        frame = null;
        consumed = 0;

        if (buffer.Length < 2)
            return false;

        var reader = new SequenceReader<byte>(buffer);

        reader.TryRead(out var b0);
        reader.TryRead(out var b1);

        var fin = (b0 & 0x80) != 0;
        var opCode = (WebSocketOpCode)(b0 & 0x0F);
        var masked = (b1 & 0x80) != 0;
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

        Span<byte> maskKey = stackalloc byte[4];
        if (masked)
        {
            if (reader.Remaining < 4)
                return false;

            reader.TryRead(out maskKey[0]);
            reader.TryRead(out maskKey[1]);
            reader.TryRead(out maskKey[2]);
            reader.TryRead(out maskKey[3]);
        }

        if (reader.Remaining < payloadLength)
            return false;

        var payload = new byte[payloadLength];
        var payloadSlice = buffer.Slice(reader.Consumed, payloadLength);
        payloadSlice.CopyTo(payload);
        reader.Advance(payloadLength);

        if (masked)
        {
            for (long i = 0; i < payload.Length; i++)
            {
                payload[i] ^= maskKey[(int)(i % 4)];
            }
        }

        frame = new WebSocketFrame
        {
            Fin = fin,
            OpCode = opCode,
            Payload = payload,
        };

        consumed = reader.Consumed;
        return true;
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
        bool mask = false
    )
    {
        var pos = 0;

        destination[pos++] = (byte)(0x80 | (byte)opCode);

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

        return destination.Length;
    }

    public static byte[] EncodeFrame(
        WebSocketOpCode opCode,
        ReadOnlySpan<byte> payload,
        bool mask = false
    )
    {
        var result = new byte[MeasureFrameSize(payload.Length, mask)];
        WriteFrame(result, opCode, payload, mask);
        return result;
    }
}
