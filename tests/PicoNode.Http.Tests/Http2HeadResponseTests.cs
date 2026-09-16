using System.IO.Pipelines;
using PicoNode.Http;
using PicoNode.Http.Internal.ConnectionRuntime;
using PicoNode.Http.Internal.Hpack;

namespace PicoNode.Http.Tests;

/// <summary>
/// RFC 7231 §4.3.2 over HTTP/2: a HEAD response carries the GET headers
/// (including content-length for buffered bodies) and END_STREAM on HEADERS —
/// no DATA frames are sent.
/// </summary>
public sealed class Http2HeadResponseTests
{
    [Test]
    public async Task Head_response_has_no_data_frames_and_keeps_content_length()
    {
        var (connection, headers, dataFrames) = await RunAsync(body: "hello"u8.ToArray());

        await Assert.That(headers).IsNotNull();
        await Assert.That(headers!.Value.EndStream).IsTrue();
        await Assert.That(headers.Value.Fields.Contains(("content-length", "5"))).IsTrue();
        await Assert.That(dataFrames).IsEqualTo(0);
    }

    [Test]
    public async Task Head_streaming_response_has_no_data_frames_and_disposes_stream()
    {
        var body = new TrackingStream("streamed"u8.ToArray());
        var (connection, headers, dataFrames) = await RunAsync(body: null, stream: body);

        await Assert.That(headers).IsNotNull();
        await Assert.That(headers!.Value.EndStream).IsTrue();
        await Assert.That(dataFrames).IsEqualTo(0);
        await Assert.That(body.Disposed).IsTrue();
    }

    private static async Task<(
        CaptureContext Connection,
        (bool EndStream, List<(string, string)> Fields)? Headers,
        int DataFrames
    )> RunAsync(byte[]? body, Stream? stream = null)
    {
        var connection = new CaptureContext();
        connection.UserState = new ConnectionRuntimeState
        {
            Protocol = ConnectionProtocol.Http2,
            ReceivedPostPrefaceFrame = true,
        };

        HttpRequestHandler handler = (_, _) =>
            ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    Headers = [new KeyValuePair<string, string>("content-type", "text/plain")],
                    Body = body ?? ReadOnlyMemory<byte>.Empty,
                    BodyStream = stream,
                }
            );

        var block = new List<byte>();
        AddLiteral(block, ":method", "HEAD");
        AddLiteral(block, ":path", "/hello");
        AddLiteral(block, ":scheme", "http");

        var frame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );

        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            handler,
            null,
            CancellationToken.None
        );

        await Task.Delay(100);

        var frames = connection
            .SentFrames.Select(ReadFrame)
            .Where(static f => f is not null)
            .ToList();
        var headersFrame = frames.FirstOrDefault(f =>
            f is { Type: Http2FrameType.Headers, StreamId: 1 }
        );
        var dataFrames = frames.Count(f => f is { Type: Http2FrameType.Data, StreamId: 1 });

        if (headersFrame is null)
            return (connection, null, dataFrames);

        var table = new HpackDynamicTable();
        var ok = HpackDecoder.TryDecode(
            headersFrame.Payload.Span,
            out var fields,
            table,
            ConnectionRuntimeState.LocalHeaderTableSize
        );

        return ok
            ? (
                connection,
                (headersFrame.Flags.HasFlag(Http2FrameFlags.EndStream), fields),
                dataFrames
            )
            : (connection, null, dataFrames);
    }

    private static void AddLiteral(List<byte> block, string name, string value)
    {
        block.Add(0x00);
        block.Add((byte)name.Length);
        block.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
        block.Add((byte)value.Length);
        block.AddRange(System.Text.Encoding.ASCII.GetBytes(value));
    }

    private static Http2Frame? ReadFrame(byte[] bytes)
    {
        if (bytes.Length < 9)
            return null;

        var length = (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
        if (bytes.Length < 9 + length)
            return null;

        var payload = new byte[length];
        Array.Copy(bytes, 9, payload, 0, length);

        return new Http2Frame
        {
            Type = (Http2FrameType)bytes[3],
            Flags = (Http2FrameFlags)bytes[4],
            StreamId = ((bytes[5] & 0x7F) << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8],
            Length = length,
            Payload = payload,
        };
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] data)
            : base(data) { }

        public bool Disposed { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            Disposed = true;
            await base.DisposeAsync();
        }
    }

    private sealed class CaptureContext : ITcpConnectionContext
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _sent = [];

        public List<byte[]> SentFrames
        {
            get
            {
                lock (_gate)
                    return [.. _sent];
            }
        }

        public long ConnectionId => 1;
        public EndPoint RemoteEndPoint =>
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234);
        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.MinValue;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.MinValue;
        public object? UserState { get; set; }
        public CancellationToken RemoteCloseToken => CancellationToken.None;
        public string? NegotiatedProtocol => null;

        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default)
        {
            var bytes = new byte[buffer.Length];
            buffer.CopyTo(bytes);
            lock (_gate)
                _sent.Add(bytes);
            return Task.CompletedTask;
        }

        public void Close() { }
    }
}
