using System.IO.Pipelines;
using System.Text;
using PicoNode.Http;
using PicoNode.Http.Internal.ConnectionRuntime;

namespace PicoNode.Http.Tests;

/// <summary>
/// RFC 7231 §4.3.2: the HEAD response has the same headers as GET but MUST NOT
/// include a message body; the Content-Length of the corresponding GET is kept.
/// </summary>
public sealed class Http1HeadResponseTests
{
    private const string HeadRequest = "HEAD /hello HTTP/1.1\r\nHost: example.com\r\n\r\n";

    [Test]
    public async Task Head_buffered_body_omits_body_and_keeps_length()
    {
        var context = new CaptureContext();
        var options = new HttpConnectionHandlerOptions
        {
            RequestHandler = static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Headers = [new KeyValuePair<string, string>("Content-Type", "text/plain")],
                        Body = "hello"u8.ToArray(),
                    }
                ),
        };

        await Http1ConnectionProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(HeadRequest)),
            options,
            options.RequestHandler,
            CancellationToken.None
        );

        var text = context.SentText;
        await Assert.That(text).StartsWith("HTTP/1.1 200");
        await Assert.That(text).Contains("Content-Length: 5");
        await Assert.That(text).DoesNotContain("hello");
    }

    [Test]
    public async Task Head_streaming_body_sends_headers_only_and_disposes_stream()
    {
        var context = new CaptureContext();
        var body = new TrackingStream("streamed"u8.ToArray());
        var options = new HttpConnectionHandlerOptions
        {
            RequestHandler = (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = body,
                    }
                ),
        };

        await Http1ConnectionProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(HeadRequest)),
            options,
            options.RequestHandler,
            CancellationToken.None
        );

        var text = context.SentText;
        await Assert.That(text).StartsWith("HTTP/1.1 200");
        await Assert.That(text).DoesNotContain("streamed");
        await Assert.That(text.EndsWith("0\r\n\r\n", StringComparison.Ordinal)).IsFalse();
        await Assert.That(body.Disposed).IsTrue();
    }

    [Test]
    public async Task Head_http10_streaming_body_sends_headers_only()
    {
        var context = new CaptureContext();
        var body = new TrackingStream("streamed"u8.ToArray());
        var options = new HttpConnectionHandlerOptions
        {
            RequestHandler = (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = body,
                    }
                ),
        };

        await Http1ConnectionProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("HEAD /hello HTTP/1.0\r\n\r\n")),
            options,
            options.RequestHandler,
            CancellationToken.None
        );

        var text = context.SentText;
        // The response version is the response's own (defaults to HTTP/1.1),
        // matching Kestrel's behaviour for HTTP/1.0 requests.
        await Assert.That(text).StartsWith("HTTP/1.1 200");
        await Assert.That(text).DoesNotContain("streamed");
        // HTTP/1.0 must not use chunked framing.
        await Assert.That(text).DoesNotContain("Transfer-Encoding: chunked");
        await Assert.That(body.Disposed).IsTrue();
    }

    [Test]
    public async Task Head_preserves_application_content_length()
    {
        var context = new CaptureContext();
        var options = new HttpConnectionHandlerOptions
        {
            RequestHandler = static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Headers =
                        [
                            new KeyValuePair<string, string>("Content-Length", "1234"),
                            new KeyValuePair<string, string>("Content-Type", "image/png"),
                        ],
                        Body = ReadOnlyMemory<byte>.Empty,
                    }
                ),
        };

        await Http1ConnectionProcessor.ProcessAsync(
            context,
            new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(HeadRequest)),
            options,
            options.RequestHandler,
            CancellationToken.None
        );

        var text = context.SentText;
        await Assert.That(text).Contains("Content-Length: 1234");
        await Assert.That(text).DoesNotContain("Content-Length: 0");
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] data)
            : base(data) { }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

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

        public string SentText
        {
            get
            {
                lock (_gate)
                    return Encoding.ASCII.GetString(_sent.SelectMany(static b => b).ToArray());
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
