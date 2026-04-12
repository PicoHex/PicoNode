namespace PicoNode.Http;

public static class Http2FrameCodec
{
    public const int FrameHeaderSize = 9;
    public const int DefaultMaxFrameSize = 16384;
    public const int MaximumAllowedFrameSize = 16777215;

    private static readonly byte[] ConnectionPreface =
        "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();

    public static ReadOnlySpan<byte> ClientPreface => ConnectionPreface;

    public static bool IsConnectionPreface(ReadOnlySpan<byte> data) =>
        data.Length >= ConnectionPreface.Length
        && data[..ConnectionPreface.Length].SequenceEqual(ConnectionPreface);

    public static bool TryReadFrame(
        ReadOnlySequence<byte> buffer,
        out Http2Frame? frame,
        out long consumed,
        int maxFrameSize = DefaultMaxFrameSize
    )
    {
        frame = null;
        consumed = 0;

        if (buffer.Length < FrameHeaderSize)
            return false;

        Span<byte> header = stackalloc byte[FrameHeaderSize];
        buffer.Slice(0, FrameHeaderSize).CopyTo(header);

        var length = (header[0] << 16) | (header[1] << 8) | header[2];

        if (length > maxFrameSize)
            return false;

        var totalSize = FrameHeaderSize + length;
        if (buffer.Length < totalSize)
            return false;

        var type = (Http2FrameType)header[3];
        var flags = (Http2FrameFlags)header[4];
        var streamId =
            ((header[5] & 0x7F) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];

        var payload = new byte[length];
        if (length > 0)
        {
            buffer.Slice(FrameHeaderSize, length).CopyTo(payload);
        }

        frame = new Http2Frame
        {
            Length = length,
            Type = type,
            Flags = flags,
            StreamId = streamId,
            Payload = payload,
        };

        consumed = totalSize;
        return true;
    }

    public static bool IsFrameTooLarge(
        ReadOnlySequence<byte> buffer,
        int maxFrameSize = DefaultMaxFrameSize
    )
    {
        if (buffer.Length < FrameHeaderSize)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[FrameHeaderSize];
        buffer.Slice(0, FrameHeaderSize).CopyTo(header);
        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        return length > maxFrameSize;
    }

    public static byte[] EncodeFrame(
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId,
        ReadOnlySpan<byte> payload
    )
    {
        var result = new byte[FrameHeaderSize + payload.Length];

        result[0] = (byte)((payload.Length >> 16) & 0xFF);
        result[1] = (byte)((payload.Length >> 8) & 0xFF);
        result[2] = (byte)(payload.Length & 0xFF);
        result[3] = (byte)type;
        result[4] = (byte)flags;
        result[5] = (byte)((streamId >> 24) & 0x7F);
        result[6] = (byte)((streamId >> 16) & 0xFF);
        result[7] = (byte)((streamId >> 8) & 0xFF);
        result[8] = (byte)(streamId & 0xFF);

        if (payload.Length > 0)
        {
            payload.CopyTo(result.AsSpan(FrameHeaderSize));
        }

        return result;
    }

    public static byte[] EncodeSettings(params ReadOnlySpan<Http2Setting> settings)
    {
        var payload = new byte[settings.Length * 6];
        for (var i = 0; i < settings.Length; i++)
        {
            var offset = i * 6;
            var s = settings[i];
            payload[offset] = (byte)((ushort)s.Id >> 8);
            payload[offset + 1] = (byte)((ushort)s.Id & 0xFF);
            payload[offset + 2] = (byte)((s.Value >> 24) & 0xFF);
            payload[offset + 3] = (byte)((s.Value >> 16) & 0xFF);
            payload[offset + 4] = (byte)((s.Value >> 8) & 0xFF);
            payload[offset + 5] = (byte)(s.Value & 0xFF);
        }

        return EncodeFrame(Http2FrameType.Settings, Http2FrameFlags.None, 0, payload);
    }

    public static byte[] EncodeSettingsAck() =>
        EncodeFrame(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, []);

    public static byte[] EncodePing(ReadOnlySpan<byte> opaqueData, bool ack = false)
    {
        Span<byte> payload = stackalloc byte[8];
        if (opaqueData.Length >= 8)
            opaqueData[..8].CopyTo(payload);
        else
            opaqueData.CopyTo(payload);

        return EncodeFrame(
            Http2FrameType.Ping,
            ack ? Http2FrameFlags.Ack : Http2FrameFlags.None,
            0,
            payload
        );
    }

    public static byte[] EncodeGoAway(int lastStreamId, Http2ErrorCode errorCode)
    {
        var payload = new byte[8];
        payload[0] = (byte)((lastStreamId >> 24) & 0x7F);
        payload[1] = (byte)((lastStreamId >> 16) & 0xFF);
        payload[2] = (byte)((lastStreamId >> 8) & 0xFF);
        payload[3] = (byte)(lastStreamId & 0xFF);
        payload[4] = (byte)(((int)errorCode >> 24) & 0xFF);
        payload[5] = (byte)(((int)errorCode >> 16) & 0xFF);
        payload[6] = (byte)(((int)errorCode >> 8) & 0xFF);
        payload[7] = (byte)((int)errorCode & 0xFF);

        return EncodeFrame(Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload);
    }

    public static IReadOnlyList<Http2Setting> ParseSettings(ReadOnlySpan<byte> payload)
    {
        var settings = new List<Http2Setting>();
        for (var i = 0; i + 5 < payload.Length; i += 6)
        {
            var id = (Http2SettingId)((payload[i] << 8) | payload[i + 1]);
            var value = (uint)(
                (payload[i + 2] << 24)
                | (payload[i + 3] << 16)
                | (payload[i + 4] << 8)
                | payload[i + 5]
            );
            settings.Add(new Http2Setting(id, value));
        }

        return settings;
    }
}
