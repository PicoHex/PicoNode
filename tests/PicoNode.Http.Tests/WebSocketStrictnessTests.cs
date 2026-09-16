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

        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void Close() => CloseCount++;
    }
}
