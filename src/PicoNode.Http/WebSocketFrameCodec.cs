using System.Buffers;

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

        byte[] maskKey = [];
        if (masked)
        {
            if (reader.Remaining < 4)
                return false;

            maskKey = new byte[4];
            reader.TryRead(out maskKey[0]);
            reader.TryRead(out maskKey[1]);
            reader.TryRead(out maskKey[2]);
            reader.TryRead(out maskKey[3]);
        }

        if (reader.Remaining < payloadLength)
            return false;

        var payload = new byte[payloadLength];
        for (long i = 0; i < payloadLength; i++)
        {
            reader.TryRead(out payload[i]);
        }

        if (masked)
        {
            for (long i = 0; i < payload.Length; i++)
            {
                payload[i] ^= maskKey[i % 4];
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

    public static byte[] EncodeFrame(
        WebSocketOpCode opCode,
        ReadOnlySpan<byte> payload,
        bool mask = false
    )
    {
        var headerSize = 2;
        if (payload.Length >= 126 && payload.Length <= 65535)
            headerSize += 2;
        else if (payload.Length > 65535)
            headerSize += 8;

        byte[] maskKey = [];
        if (mask)
        {
            maskKey = new byte[4];
            Random.Shared.NextBytes(maskKey);
            headerSize += 4;
        }

        var result = new byte[headerSize + payload.Length];
        var pos = 0;

        result[pos++] = (byte)(0x80 | (byte)opCode);

        if (payload.Length < 126)
        {
            result[pos++] = (byte)((mask ? 0x80 : 0) | payload.Length);
        }
        else if (payload.Length <= 65535)
        {
            result[pos++] = (byte)((mask ? 0x80 : 0) | 126);
            result[pos++] = (byte)(payload.Length >> 8);
            result[pos++] = (byte)(payload.Length & 0xFF);
        }
        else
        {
            result[pos++] = (byte)((mask ? 0x80 : 0) | 127);
            var len = (long)payload.Length;
            for (var i = 7; i >= 0; i--)
            {
                result[pos++] = (byte)((len >> (i * 8)) & 0xFF);
            }
        }

        if (mask)
        {
            maskKey.CopyTo(result.AsSpan(pos));
            pos += 4;
        }

        payload.CopyTo(result.AsSpan(pos));

        if (mask)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                result[pos + i] ^= maskKey[i % 4];
            }
        }

        return result;
    }
}
