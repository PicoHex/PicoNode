namespace PicoNode.Http;

public static class Http2FrameCodec
{
    public const int FrameHeaderSize = 9;
    public const int DefaultMaxFrameSize = 16384;

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

    // ── Zero-alloc writers (Span<byte> destination) ──────────────────

    /// <summary>Writes a complete HTTP/2 frame into the destination span.
    /// Returns the number of bytes written. Throws if destination is too small.</summary>
    public static int WriteFrame(
        Span<byte> destination,
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId,
        ReadOnlySpan<byte> payload
    )
    {
        WriteFrameHeader(destination, payload.Length, type, flags, streamId);
        if (payload.Length > 0)
        {
            payload.CopyTo(destination.Slice(FrameHeaderSize));
        }

        return FrameHeaderSize + payload.Length;
    }

    /// <summary>Writes only the 9-byte frame header into destination.
    /// Returns 9. Throws if destination.Length &lt; 9.</summary>
    public static int WriteFrameHeader(
        Span<byte> destination,
        int payloadLength,
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId
    )
    {
        destination[0] = (byte)((payloadLength >> 16) & 0xFF);
        destination[1] = (byte)((payloadLength >> 8) & 0xFF);
        destination[2] = (byte)(payloadLength & 0xFF);
        destination[3] = (byte)type;
        destination[4] = (byte)flags;
        destination[5] = (byte)((streamId >> 24) & 0x7F);
        destination[6] = (byte)((streamId >> 16) & 0xFF);
        destination[7] = (byte)((streamId >> 8) & 0xFF);
        destination[8] = (byte)(streamId & 0xFF);
        return FrameHeaderSize;
    }

    /// <summary>Writes a complete SETTINGS frame (header + variable payload).
    /// Returns total bytes written.</summary>
    public static int WriteSettings(
        Span<byte> destination,
        ReadOnlySpan<Http2Setting> settings
    )
    {
        var payloadLength = settings.Length * 6;
        WriteFrameHeader(
            destination,
            payloadLength,
            Http2FrameType.Settings,
            Http2FrameFlags.None,
            0
        );

        var pos = FrameHeaderSize;
        for (var i = 0; i < settings.Length; i++)
        {
            var s = settings[i];
            destination[pos] = (byte)((ushort)s.Id >> 8);
            destination[pos + 1] = (byte)((ushort)s.Id & 0xFF);
            destination[pos + 2] = (byte)((s.Value >> 24) & 0xFF);
            destination[pos + 3] = (byte)((s.Value >> 16) & 0xFF);
            destination[pos + 4] = (byte)((s.Value >> 8) & 0xFF);
            destination[pos + 5] = (byte)(s.Value & 0xFF);
            pos += 6;
        }

        return pos;
    }

    /// <summary>Writes a SETTINGS ACK frame into destination (9 bytes).</summary>
    public static int WriteSettingsAck(Span<byte> destination)
    {
        WriteFrameHeader(
            destination,
            0,
            Http2FrameType.Settings,
            Http2FrameFlags.Ack,
            0
        );
        return FrameHeaderSize;
    }

    /// <summary>Writes a PING frame into destination (17 bytes).</summary>
    public static int WritePing(
        Span<byte> destination,
        ReadOnlySpan<byte> opaqueData,
        bool ack = false
    )
    {
        WriteFrameHeader(
            destination,
            8,
            Http2FrameType.Ping,
            ack ? Http2FrameFlags.Ack : Http2FrameFlags.None,
            0
        );
        if (opaqueData.Length >= 8)
            opaqueData[..8].CopyTo(destination.Slice(FrameHeaderSize));
        else
            opaqueData.CopyTo(destination.Slice(FrameHeaderSize));
        return FrameHeaderSize + 8;
    }

    /// <summary>Writes a GOAWAY frame into destination (17 bytes).</summary>
    public static int WriteGoAway(
        Span<byte> destination,
        int lastStreamId,
        Http2ErrorCode errorCode
    )
    {
        WriteFrameHeader(
            destination,
            8,
            Http2FrameType.GoAway,
            Http2FrameFlags.None,
            0
        );
        var pos = FrameHeaderSize;
        destination[pos] = (byte)((lastStreamId >> 24) & 0x7F);
        destination[pos + 1] = (byte)((lastStreamId >> 16) & 0xFF);
        destination[pos + 2] = (byte)((lastStreamId >> 8) & 0xFF);
        destination[pos + 3] = (byte)(lastStreamId & 0xFF);
        destination[pos + 4] = (byte)(((int)errorCode >> 24) & 0xFF);
        destination[pos + 5] = (byte)(((int)errorCode >> 16) & 0xFF);
        destination[pos + 6] = (byte)(((int)errorCode >> 8) & 0xFF);
        destination[pos + 7] = (byte)((int)errorCode & 0xFF);
        return FrameHeaderSize + 8;
    }

    // ── Convenience allocators (kept for test compatibility) ────────

    public static byte[] EncodeFrame(
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId,
        ReadOnlySpan<byte> payload
    )
    {
        var result = new byte[FrameHeaderSize + payload.Length];
        WriteFrame(result, type, flags, streamId, payload);
        return result;
    }

    public static byte[] EncodeSettings(params ReadOnlySpan<Http2Setting> settings)
    {
        var result = new byte[FrameHeaderSize + settings.Length * 6];
        WriteSettings(result, settings);
        return result;
    }

    public static byte[] EncodeSettingsAck()
    {
        var result = new byte[FrameHeaderSize];
        WriteSettingsAck(result);
        return result;
    }

    public static byte[] EncodePing(ReadOnlySpan<byte> opaqueData, bool ack = false)
    {
        var result = new byte[FrameHeaderSize + 8];
        WritePing(result, opaqueData, ack);
        return result;
    }

    public static byte[] EncodeGoAway(int lastStreamId, Http2ErrorCode errorCode)
    {
        var result = new byte[FrameHeaderSize + 8];
        WriteGoAway(result, lastStreamId, errorCode);
        return result;
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
