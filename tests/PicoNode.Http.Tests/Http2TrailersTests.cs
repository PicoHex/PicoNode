using System.IO.Pipelines;
using PicoNode.Http;
using PicoNode.Http.Internal.ConnectionRuntime;

namespace PicoNode.Http.Tests;

/// <summary>
/// RFC 7540 §8.1: a request body may be followed by trailer HEADERS carrying
/// END_STREAM. The buffered DATA must be delivered to the handler and
/// content-length must be validated against it — not against zero.
/// </summary>
public sealed class Http2TrailersTests
{
    [Test]
    public async Task Trailers_with_content_length_deliver_body()
    {
        var (connection, received) = await RunTrailerFlowAsync(withContentLength: true);

        await Assert.That(connection.LastRstStreamCode).IsNull();
        await Assert.That(received).IsEqualTo("test");
    }

    [Test]
    public async Task Trailers_without_content_length_deliver_body()
    {
        var (connection, received) = await RunTrailerFlowAsync(withContentLength: false);

        await Assert.That(connection.LastRstStreamCode).IsNull();
        await Assert.That(received).IsEqualTo("test");
    }

    private static async Task<(CaptureContext, string?)> RunTrailerFlowAsync(bool withContentLength)
    {
        var connection = new CaptureContext();
        connection.UserState = new ConnectionRuntimeState
        {
            Protocol = ConnectionProtocol.Http2,
            ReceivedPostPrefaceFrame = true,
        };

        string? received = null;
        HttpRequestHandler handler = async (req, ct) =>
        {
            if (req.BodyStream is not null)
            {
                using var reader = new StreamReader(req.BodyStream);
                received = await reader.ReadToEndAsync(ct);
            }
            return new HttpResponse { StatusCode = 200 };
        };

        // HEADERS (no END_STREAM): POST /foo (+ content-length when requested)
        var block = new List<byte>();
        AddLiteral(block, ":method", "POST");
        AddLiteral(block, ":path", "/foo");
        AddLiteral(block, ":scheme", "http");
        if (withContentLength)
            AddLiteral(block, "content-length", "4");

        var headers = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders,
            1,
            block.ToArray()
        );
        await ProcessAsync(connection, headers, handler);

        // DATA "test" (no flags)
        var data = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Data,
            Http2FrameFlags.None,
            1,
            "test"u8.ToArray()
        );
        await ProcessAsync(connection, data, handler);

        // Trailers HEADERS with END_STREAM
        var trailers = new List<byte>();
        AddLiteral(trailers, "x-trailer", "1");
        var trailerFrame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            trailers.ToArray()
        );
        await ProcessAsync(connection, trailerFrame, handler);

        return (connection, received);
    }

    private static Task ProcessAsync(
        CaptureContext connection,
        byte[] frame,
        HttpRequestHandler handler
    ) =>
        Http2ConnectionProcessor
            .ProcessAsync(
                connection,
                new ReadOnlySequence<byte>(frame),
                sendInitialSettings: false,
                handler,
                null,
                CancellationToken.None
            )
            .AsTask();

    private static void AddLiteral(List<byte> block, string name, string value)
    {
        block.Add(0x00); // literal without indexing, new name
        block.Add((byte)name.Length);
        block.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
        block.Add((byte)value.Length);
        block.AddRange(System.Text.Encoding.ASCII.GetBytes(value));
    }

    private sealed class CaptureContext : ITcpConnectionContext
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _sent = [];

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

        public Http2ErrorCode? LastRstStreamCode
        {
            get
            {
                Http2ErrorCode? code = null;
                lock (_gate)
                {
                    foreach (var bytes in _sent)
                    {
                        if (bytes.Length < 13)
                            continue;
                        if ((Http2FrameType)bytes[3] != Http2FrameType.RstStream)
                            continue;
                        code = (Http2ErrorCode)(
                            (bytes[9] << 24) | (bytes[10] << 16) | (bytes[11] << 8) | bytes[12]
                        );
                    }
                }
                return code;
            }
        }
    }
}
