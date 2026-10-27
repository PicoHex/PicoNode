using System.IO.Pipelines;
using PicoNode.Http;
using PicoNode.Http.Internal.ConnectionRuntime;

namespace PicoNode.Http.Tests;

/// <summary>
/// RFC 6455 §5.2 / RFC 7692 strictness: unknown opcodes, RSV bits that were
/// never negotiated, and RSV1 on control frames MUST fail the connection.
/// </summary>
public sealed class WebSocketStrictnessTests
{
    [Test]
    public async Task Reserved_non_control_opcode_fails_connection()
    {
        var context = new RecordingConnectionContext();
        var frame = WebSocketFrameCodec.EncodeFrame(
            (WebSocketOpCode)0x3,
            "x"u8.ToArray(),
            mask: true
        );

        await ProcessAsync(context, frame);

        await Assert.That(context.CloseCount).IsEqualTo(1);
    }

    [Test]
    public async Task Reserved_control_opcode_fails_connection()
    {
        var context = new RecordingConnectionContext();
        var frame = WebSocketFrameCodec.EncodeFrame(
            (WebSocketOpCode)0xB,
            "x"u8.ToArray(),
            mask: true
        );

        await ProcessAsync(context, frame);

        await Assert.That(context.CloseCount).IsEqualTo(1);
    }

    [Test]
    public async Task Rsv1_without_negotiated_compression_fails_connection()
    {
        var context = new RecordingConnectionContext();
        var delivered = false;
        var frame = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Text,
            "hello"u8.ToArray(),
            mask: true,
            rsv1: true
        );

        await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(frame),
            (_, _, _) =>
            {
                delivered = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None,
            new WebSocketMessageProcessorState { CompressionNegotiated = false }
        );

        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(delivered).IsFalse();
    }

    [Test]
    public async Task Rsv1_on_control_frame_fails_connection()
    {
        var context = new RecordingConnectionContext();
        var frame = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Ping,
            "ping"u8.ToArray(),
            mask: true,
            rsv1: true
        );

        await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(frame),
            static (_, _, _) => ValueTask.CompletedTask,
            CancellationToken.None,
            new WebSocketMessageProcessorState { CompressionNegotiated = true }
        );

        await Assert.That(context.CloseCount).IsEqualTo(1);
    }

    [Test]
    public async Task Fin_with_null_handler_resets_fragment_state()
    {
        var context = new RecordingConnectionContext();
        var state = new WebSocketMessageProcessorState();
        var first = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Text,
            "one"u8.ToArray(),
            mask: true
        );

        // No handler: the completed message is dropped, but its fragment state
        // must be reset so the next data frame starts a new message.
        await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(first),
            null,
            CancellationToken.None,
            state
        );

        string? delivered = null;
        var second = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Text,
            "two"u8.ToArray(),
            mask: true
        );
        await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(second),
            (message, _, _) =>
            {
                delivered = System.Text.Encoding.UTF8.GetString(message.Payload.Span);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None,
            state
        );

        await Assert.That(context.CloseCount).IsEqualTo(0);
        await Assert.That(delivered).IsEqualTo("two");
    }

    // T1.6: configured limits (RFC 6455 §7.4.1), minimal length form (§5.2), registered
    // close codes (§7.4.1 + IANA), version negotiation (§4.2.2), stop-after-close.

    [Test]
    public async Task Deflate_path_honours_configured_max_message_size()
    {
        const int limit = 1024;
        var context = new RecordingConnectionContext();
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = (request, _) =>
                {
                    var upgrade = WebSocketUpgrade.TryUpgrade(request)!;
                    // RFC 7692 §6: only the echoed extension is negotiated.
                    upgrade.Headers.Add("Sec-WebSocket-Extensions", "permessage-deflate");
                    return ValueTask.FromResult(upgrade);
                },
                WebSocketMessageHandler = static (_, _, _) => ValueTask.CompletedTask,
                WebSocketMaxMessageSize = limit,
            }
        );

        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(HandshakeRequest(deflate: true)),
            CancellationToken.None
        );

        // 2 KB of zeros compresses to a couple of dozen bytes, so the frame is far
        // below the limit and only the post-inflate bound can catch it.
        var compressor = new WebSocketMessageProcessorState { CompressionNegotiated = true };
        var compressed = compressor.Compress(new byte[2048]);
        await Assert.That(compressed.Length).IsLessThan(limit);

        var frame = BuildMaskedFrame(WebSocketOpCode.Text, compressed, rsv1: true);
        await handler.OnReceivedAsync(
            context,
            new ReadOnlySequence<byte>(frame),
            CancellationToken.None
        );

        await Assert
            .That(context.Sent.Count)
            .IsEqualTo(2)
            .Because("a 1009 close frame must follow the 101 handshake");
        await Assert
            .That(CloseCodeOfLastFrame(context))
            .IsEqualTo((ushort)1009)
            .Because(
                "WebSocketMaxMessageSize must reach the permessage-deflate upgrade path"
                    + " (RFC 6455 §7.4.1)"
            );
    }

    [Test]
    public async Task Non_minimal_length_encoding_is_rejected()
    {
        var context = new RecordingConnectionContext();
        var payload = "hello"u8.ToArray();
        byte[] mask = [0x11, 0x22, 0x33, 0x44];
        var masked = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i++)
            masked[i] = (byte)(payload[i] ^ mask[i % 4]);

        // 5-byte payload announced with the 126 form. RFC 6455 §5.2: "the minimal
        // number of bytes MUST be used to encode the length" - a bare 5 here.
        byte[] frame = [0x81, 0x80 | 126, 0x00, 0x05, .. mask, .. masked];

        await ProcessAsync(context, frame);

        await Assert
            .That(context.Sent.Count)
            .IsEqualTo(1)
            .Because("the invalid header must be answered with a close frame, not ignored");
        await Assert
            .That(CloseCodeOfLastFrame(context))
            .IsEqualTo((ushort)1002)
            .Because("a non-minimal length encoding is a protocol error, not a size error");
    }

    [Test]
    public async Task Close_code_1013_is_accepted()
    {
        var context = new RecordingConnectionContext();

        // 1013 = Try Again Later (IANA WebSocket Close Code Number registry).
        var frame = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Close,
            [(byte)0x03, (byte)0xF5],
            mask: true
        );

        await ProcessAsync(context, frame);

        await Assert
            .That(CloseCodeOfLastFrame(context))
            .IsEqualTo((ushort)1013)
            .Because(
                "1012/1013/1014 are IANA-registered close codes (RFC 6455 §7.4.1)"
                    + " and must be echoed, not answered with 1002"
            );
    }

    [Test]
    public async Task Version_mismatch_returns_426()
    {
        var response = WebSocketUpgrade.TryUpgrade(UpgradeRequest(version: "8"));

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(426);
        // The caller can send this response as-is.
        var wire = Encoding.ASCII.GetString(HttpResponseSerializer.Serialize(response).ToArray());
        await Assert
            .That(wire)
            .IsEqualTo(
                "HTTP/1.1 426 Upgrade Required\r\n"
                    + "Sec-WebSocket-Version: 13\r\n"
                    + "Upgrade: websocket\r\n"
                    + "Content-Length: 0\r\n"
                    + "\r\n"
            );

        await Assert
            .That(response.Headers["Sec-WebSocket-Version"])
            .IsEqualTo("13")
            .Because("RFC 6455 §4.2.2: the 426 must name the version(s) the server understands");
    }

    [Test]
    public async Task Frames_after_a_close_are_not_processed()
    {
        var context = new RecordingConnectionContext();
        var state = new WebSocketMessageProcessorState
        {
            CompressionNegotiated = true,
            MaxMessageSize = 1024,
        };

        // The first frame inflates past the limit and is closed with 1009; the
        // second frame is already buffered and must NOT be delivered after that.
        var compressed = state.Compress(new byte[2048]);
        var oversized = BuildMaskedFrame(WebSocketOpCode.Text, compressed, rsv1: true);
        var afterClose = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Text,
            "after"u8.ToArray(),
            mask: true
        );
        var delivered = 0;

        await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>([.. oversized, .. afterClose]),
            (_, _, _) =>
            {
                delivered++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None,
            state
        );

        await Assert.That(CloseCodeOfLastFrame(context)).IsEqualTo((ushort)1009);
        await Assert
            .That(delivered)
            .IsEqualTo(0)
            .Because("nothing may be delivered after the close frame was written");
    }

    private static Task ProcessAsync(RecordingConnectionContext context, byte[] frame) =>
        WebSocketMessageProcessor
            .ProcessAsync(
                context,
                new ReadOnlySequence<byte>(frame),
                static (_, _, _) => ValueTask.CompletedTask,
                CancellationToken.None,
                new WebSocketMessageProcessorState()
            )
            .AsTask();

    /// <summary>Reads the status code out of the last frame the server wrote.</summary>
    private static ushort CloseCodeOfLastFrame(RecordingConnectionContext context)
    {
        var read = WebSocketFrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.Sent[^1]),
            out var frame,
            out _
        );

        // 0 is not a legal close code on the wire, so a missing close frame fails
        // the callers' assertions loudly instead of silently passing.
        if (!read || frame is null || frame.OpCode != WebSocketOpCode.Close)
            return 0;

        return frame.Payload.Length >= 2
            ? (ushort)((frame.Payload.Span[0] << 8) | frame.Payload.Span[1])
            : (ushort)0;
    }

    private static byte[] HandshakeRequest(bool deflate)
    {
        var text =
            "GET /ws HTTP/1.1\r\n"
            + "Host: localhost\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
            + "Sec-WebSocket-Version: 13\r\n";

        if (deflate)
            text += "Sec-WebSocket-Extensions: permessage-deflate\r\n";

        return Encoding.ASCII.GetBytes(text + "\r\n");
    }

    private static HttpRequest UpgradeRequest(string version)
    {
        var headerFields = new List<KeyValuePair<string, string>>
        {
            new("Host", "localhost"),
            new("Upgrade", "websocket"),
            new("Connection", "Upgrade"),
            new("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ=="),
            new("Sec-WebSocket-Version", version),
        };

        return new HttpRequest
        {
            Method = "GET",
            Target = "/ws",
            Version = HttpVersion.Http11,
            HeaderFields = headerFields,
            Headers = headerFields.ToDictionary(
                h => h.Key,
                h => h.Value,
                StringComparer.OrdinalIgnoreCase
            ),
            Body = ReadOnlyMemory<byte>.Empty,
        };
    }

    /// <summary>Builds a masked client frame whose length uses the minimal form.</summary>
    private static byte[] BuildMaskedFrame(
        WebSocketOpCode opCode,
        byte[] payload,
        bool rsv1 = false
    )
    {
        var header = new List<byte> { (byte)(0x80 | (rsv1 ? 0x40 : 0) | (byte)opCode) };
        if (payload.Length < 126)
            header.Add((byte)(0x80 | payload.Length));
        else if (payload.Length <= ushort.MaxValue)
            header.AddRange([
                0x80 | 126,
                (byte)(payload.Length >> 8),
                (byte)(payload.Length & 0xFF),
            ]);
        else
            throw new NotSupportedException("test payload too large");

        byte[] mask = [0x11, 0x22, 0x33, 0x44];
        header.AddRange(mask);

        var masked = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i++)
            masked[i] = (byte)(payload[i] ^ mask[i % 4]);

        return [.. header, .. masked];
    }

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId => 1;
        public EndPoint RemoteEndPoint =>
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 12345);
        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UnixEpoch;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.UnixEpoch;
        public object? UserState { get; set; }
        public CancellationToken RemoteCloseToken => CancellationToken.None;
        public string? NegotiatedProtocol => null;
        public int CloseCount { get; private set; }

        /// <summary>Every buffer written to the connection, in order.</summary>
        public List<byte[]> Sent { get; } = [];

        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default)
        {
            Sent.Add(buffer.ToArray());
            return Task.CompletedTask;
        }

        public void Close() => CloseCount++;
    }
}
