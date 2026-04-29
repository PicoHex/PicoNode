using System.Net;
using System.Text;
using PicoNode.Http.Internal.ConnectionRuntime;

namespace PicoNode.Http.Tests;

public sealed class WebSocketMessageProcessorTests
{
    [Test]
    public async Task Single_text_frame_invokes_handler_with_correct_payload()
    {
        var payload = "Hello, WebSocket!"u8.ToArray();
        var encoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Text, payload, mask: true);
        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(encoded),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(encoded.Length);
        await Assert.That(capturedMessage).IsNotNull();
        await Assert.That(capturedMessage!.OpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert
            .That(Encoding.UTF8.GetString(capturedMessage.Payload.Span))
            .IsEqualTo("Hello, WebSocket!");
        await Assert.That(capturedMessage.IsEndOfMessage).IsTrue();
    }

    [Test]
    public async Task Fragmented_text_frames_assembled_and_handler_called_once_on_fin()
    {
        var payload1 = "Hello, "u8.ToArray();
        var payload2 = "World!"u8.ToArray();
        var frame1 = CreateFrame(WebSocketOpCode.Text, payload1, fin: false, mask: true);
        var frame2 = CreateFrame(WebSocketOpCode.Continuation, payload2, fin: true, mask: true);

        var combined = new byte[frame1.Length + frame2.Length];
        frame1.CopyTo(combined, 0);
        frame2.CopyTo(combined, frame1.Length);

        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(combined),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(combined.Length);
        await Assert.That(capturedMessage).IsNotNull();
        await Assert.That(capturedMessage!.OpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert
            .That(Encoding.UTF8.GetString(capturedMessage.Payload.Span))
            .IsEqualTo("Hello, World!");
        await Assert.That(capturedMessage.IsEndOfMessage).IsTrue();
    }

    [Test]
    public async Task Ping_frame_sends_pong_automatically()
    {
        var pingPayload = "are-you-there"u8.ToArray();
        var encoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Ping, pingPayload);
        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(encoded),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(encoded.Length);
        await Assert.That(context.SendCount).IsEqualTo(1);
        await Assert.That(context.CloseCount).IsEqualTo(0);
        await Assert.That(capturedMessage).IsNull();

        var success = WebSocketFrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.LastSent),
            out var pong,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(pong!.OpCode).IsEqualTo(WebSocketOpCode.Pong);
        await Assert.That(Encoding.UTF8.GetString(pong.Payload.Span)).IsEqualTo("are-you-there");
    }

    [Test]
    public async Task Close_frame_sends_close_response_and_closes_connection()
    {
        var encoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Close, []);
        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(encoded),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(encoded.Length);
        await Assert.That(context.SendCount).IsEqualTo(1);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(capturedMessage).IsNull();

        var success = WebSocketFrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.LastSent),
            out var closeFrame,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(closeFrame!.OpCode).IsEqualTo(WebSocketOpCode.Close);
    }

    [Test]
    public async Task Binary_frame_invokes_handler_with_binary_opcode()
    {
        var payload = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var encoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Binary, payload, mask: true);
        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(encoded),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(encoded.Length);
        await Assert.That(capturedMessage).IsNotNull();
        await Assert.That(capturedMessage!.OpCode).IsEqualTo(WebSocketOpCode.Binary);
        await Assert
            .That(capturedMessage.Payload.Span.ToArray())
            .IsEquivalentTo(new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE });
        await Assert.That(capturedMessage.IsEndOfMessage).IsTrue();
    }

    [Test]
    public async Task Continuation_frames_assemble_correctly()
    {
        var part1 = new byte[] { 0xAA, 0xBB };
        var part2 = new byte[] { 0xCC, 0xDD, 0xEE };
        var frame1 = CreateFrame(WebSocketOpCode.Binary, part1, fin: false, mask: true);
        var frame2 = CreateFrame(WebSocketOpCode.Continuation, part2, fin: true, mask: true);

        var combined = new byte[frame1.Length + frame2.Length];
        frame1.CopyTo(combined, 0);
        frame2.CopyTo(combined, frame1.Length);

        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(combined),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(combined.Length);
        await Assert.That(capturedMessage).IsNotNull();
        await Assert.That(capturedMessage!.OpCode).IsEqualTo(WebSocketOpCode.Binary);
        await Assert
            .That(capturedMessage.Payload.Span.ToArray())
            .IsEquivalentTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE });
        await Assert.That(capturedMessage.IsEndOfMessage).IsTrue();
    }

    [Test]
    public async Task Incomplete_frame_returns_start_position()
    {
        var partial = new byte[] { 0x81 };
        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(partial),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(0);
        await Assert.That(context.SendCount).IsEqualTo(0);
        await Assert.That(capturedMessage).IsNull();
    }

    [Test]
    public async Task Null_handler_still_processes_control_frames()
    {
        var ping = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Ping, "test"u8);
        var context = new RecordingConnectionContext();

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(ping),
            null,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(ping.Length);
        await Assert.That(context.SendCount).IsEqualTo(1);

        var success = WebSocketFrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(context.LastSent),
            out var pong,
            out _
        );
        await Assert.That(success).IsTrue();
        await Assert.That(pong!.OpCode).IsEqualTo(WebSocketOpCode.Pong);
    }

    [Test]
    public async Task Multiple_complete_frames_in_single_buffer_all_processed()
    {
        var textPayload = "msg1"u8.ToArray();
        var textFrame = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Text,
            textPayload,
            mask: true
        );
        var binaryPayload = new byte[] { 0x01, 0x02 };
        var binaryFrame = WebSocketFrameCodec.EncodeFrame(
            WebSocketOpCode.Binary,
            binaryPayload,
            mask: true
        );

        var combined = new byte[textFrame.Length + binaryFrame.Length];
        textFrame.CopyTo(combined, 0);
        binaryFrame.CopyTo(combined, textFrame.Length);

        var context = new RecordingConnectionContext();
        var capturedMessages = new List<WebSocketMessage>();

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessages.Add(message);
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(combined),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(combined.Length);
        await Assert.That(capturedMessages).Count().IsEqualTo(2);
        await Assert.That(capturedMessages[0].OpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert
            .That(Encoding.UTF8.GetString(capturedMessages[0].Payload.Span))
            .IsEqualTo("msg1");
        await Assert.That(capturedMessages[1].OpCode).IsEqualTo(WebSocketOpCode.Binary);
        await Assert
            .That(capturedMessages[1].Payload.Span.ToArray())
            .IsEquivalentTo(new byte[] { 0x01, 0x02 });
    }

    private static byte[] CreateFrame(
        WebSocketOpCode opCode,
        ReadOnlySpan<byte> payload,
        bool fin,
        bool mask
    )
    {
        var headerSize = 2;
        if (payload.Length is >= 126 and <= 65535)
            headerSize += 2;
        else if (payload.Length > 65535)
            headerSize += 8;

        if (mask)
            headerSize += 4;

        var result = new byte[headerSize + payload.Length];
        var pos = 0;

        var b0 = (byte)((fin ? 0x80 : 0x00) | (byte)opCode);
        result[pos++] = b0;

        var maskFlag = mask ? (byte)0x80 : (byte)0x00;

        if (payload.Length < 126)
        {
            result[pos++] = (byte)(maskFlag | payload.Length);
        }
        else if (payload.Length <= 65535)
        {
            result[pos++] = (byte)(maskFlag | 126);
            result[pos++] = (byte)(payload.Length >> 8);
            result[pos++] = (byte)(payload.Length & 0xFF);
        }
        else
        {
            result[pos++] = (byte)(maskFlag | 127);
            var len = (long)payload.Length;
            for (var i = 7; i >= 0; i--)
            {
                result[pos++] = (byte)((len >> (i * 8)) & 0xFF);
            }
        }

        if (mask)
        {
            var maskKey = new byte[4];
            Random.Shared.NextBytes(maskKey);
            maskKey.CopyTo(result.AsSpan(pos));
            pos += 4;

            payload.CopyTo(result.AsSpan(pos));
            for (var i = 0; i < payload.Length; i++)
            {
                result[pos + i] ^= maskKey[i % 4];
            }
        }
        else
        {
            payload.CopyTo(result.AsSpan(pos));
        }

        return result;
    }

    [Test]
    public async Task Fragmented_message_across_multiple_receive_calls_assembled_via_state()
    {
        var payload1 = "Hello, "u8.ToArray();
        var payload2 = "cross-call World!"u8.ToArray();
        var frame1 = CreateFrame(WebSocketOpCode.Text, payload1, fin: false, mask: true);
        var frame2 = CreateFrame(WebSocketOpCode.Continuation, payload2, fin: true, mask: true);

        var context = new RecordingConnectionContext();
        var state = new WebSocketMessageProcessorState();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        // First call: send partial frame
        var consumed1 = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(frame1),
            handler,
            CancellationToken.None,
            state
        );

        await Assert.That(consumed1.GetInteger()).IsEqualTo(frame1.Length);
        await Assert.That(capturedMessage).IsNull();
        await Assert.That(state.MessageOpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert.That(state.PayloadBuffer.WrittenCount).IsEqualTo(payload1.Length);

        // Second call: send continuation frame
        var consumed2 = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(frame2),
            handler,
            CancellationToken.None,
            state
        );

        await Assert.That(consumed2.GetInteger()).IsEqualTo(frame2.Length);
        await Assert.That(capturedMessage).IsNotNull();
        await Assert.That(capturedMessage!.OpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert
            .That(Encoding.UTF8.GetString(capturedMessage.Payload.Span))
            .IsEqualTo("Hello, cross-call World!");
        await Assert.That(capturedMessage.IsEndOfMessage).IsTrue();
        await Assert.That(state.MessageOpCode).IsNull();
        await Assert.That(state.PayloadBuffer.WrittenCount).IsEqualTo(0);
    }

    [Test]
    public async Task Client_ping_during_fragmentation_sends_pong_and_assembly_continues()
    {
        var textPayload = "Data"u8.ToArray();
        var continuationPayload = "More"u8.ToArray();
        var textFrame = CreateFrame(WebSocketOpCode.Text, textPayload, fin: false, mask: true);
        var pingFrame = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Ping, "ping"u8);
        var continuationFrame = CreateFrame(
            WebSocketOpCode.Continuation,
            continuationPayload,
            fin: true,
            mask: true
        );

        var combined = new byte[textFrame.Length + pingFrame.Length + continuationFrame.Length];
        textFrame.CopyTo(combined, 0);
        pingFrame.CopyTo(combined, textFrame.Length);
        continuationFrame.CopyTo(combined, textFrame.Length + pingFrame.Length);

        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(combined),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(combined.Length);
        await Assert.That(context.SendCount).IsEqualTo(1);
        await Assert.That(capturedMessage).IsNotNull();
        await Assert
            .That(Encoding.UTF8.GetString(capturedMessage!.Payload.Span))
            .IsEqualTo("DataMore");
        await Assert.That(capturedMessage.IsEndOfMessage).IsTrue();
    }

    [Test]
    public async Task Client_close_during_fragmentation_closes_connection_discards_partial_message()
    {
        var textPayload = "Incomplete"u8.ToArray();
        var textFrame = CreateFrame(WebSocketOpCode.Text, textPayload, fin: false, mask: true);
        var closeFrame = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Close, "bye"u8);

        var combined = new byte[textFrame.Length + closeFrame.Length];
        textFrame.CopyTo(combined, 0);
        closeFrame.CopyTo(combined, textFrame.Length);

        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(combined),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(combined.Length);
        await Assert.That(context.SendCount).IsEqualTo(1);
        await Assert.That(context.CloseCount).IsEqualTo(1);
        await Assert.That(capturedMessage).IsNull();
    }

    [Test]
    public async Task Empty_text_frame_invokes_handler_with_empty_payload()
    {
        var encoded = CreateFrame(WebSocketOpCode.Text, [], fin: true, mask: true);
        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(encoded),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(encoded.Length);
        await Assert.That(capturedMessage).IsNotNull();
        await Assert.That(capturedMessage!.OpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert.That(capturedMessage.Payload.Length).IsEqualTo(0);
        await Assert.That(capturedMessage.IsEndOfMessage).IsTrue();
    }

    [Test]
    public async Task Continuation_without_initial_frame_ignored()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var frame = CreateFrame(WebSocketOpCode.Continuation, payload, fin: true, mask: true);
        var context = new RecordingConnectionContext();
        WebSocketMessage? capturedMessage = null;

        WebSocketMessageHandler handler = (message, connection, ct) =>
        {
            capturedMessage = message;
            return ValueTask.CompletedTask;
        };

        var consumed = await WebSocketMessageProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(frame),
            handler,
            CancellationToken.None
        );

        await Assert.That(consumed.GetInteger()).IsEqualTo(frame.Length);
        await Assert.That(capturedMessage).IsNull();
        await Assert.That(context.SendCount).IsEqualTo(0);
        await Assert.That(context.CloseCount).IsEqualTo(0);
    }

    private sealed class RecordingConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId => 1;

        public IPEndPoint RemoteEndPoint => new(IPAddress.Loopback, 12345);

        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.UnixEpoch;

        public DateTimeOffset LastActivityUtc => DateTimeOffset.UnixEpoch;

        public byte[] LastSent { get; private set; } = [];

        public int SendCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task SendAsync(
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            LastSent = buffer.ToArray();
            SendCount++;
            return Task.CompletedTask;
        }

        public void Close()
        {
            CloseCount++;
        }
    }
}
