namespace PicoNode.Http.Tests;

using System.Net;
using System.Text;
using PicoNode.Http.Internal.ConnectionRuntime;
using PicoNode.Http.Internal.Hpack;

public sealed class Http2StreamHandlerTests
{
    private static readonly byte[] MinimalHpackPayload = BuildMinimalHpack("GET", "/foo");

    [Test]
    public async Task HeadersFrame_GetFoo_HandlerCalled_ResponseHeadersSentWithStatus200()
    {
        var connection = new TestTcpConnectionContext();
        var handlerCalled = false;
        HttpRequest? capturedRequest = null;

        HttpRequestHandler handler = (req, ct) =>
        {
            handlerCalled = true;
            capturedRequest = req;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsFalse();
        await Assert.That(handlerCalled).IsTrue();
        await Assert.That(capturedRequest).IsNotNull();
        await Assert.That(capturedRequest!.Method).IsEqualTo("GET");
        await Assert.That(capturedRequest.Target).IsEqualTo("/foo");
        await Assert.That(capturedRequest.Version).IsEqualTo("HTTP/2");

        await Assert.That(connection.SentFrames.Count).IsEqualTo(1);

        var sentHeaders = connection.SentFrames[0];
        var decoded = DecodeHeadersFrame(sentHeaders, out var headers, out var flags);

        await Assert.That(decoded).IsTrue();
        await Assert.That(flags.HasFlag(Http2FrameFlags.EndHeaders)).IsTrue();
        await Assert.That(flags.HasFlag(Http2FrameFlags.EndStream)).IsTrue();
        await Assert.That(headers!.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(headers[0].Item1).IsEqualTo(":status");
        await Assert.That(headers[0].Item2).IsEqualTo("200");
    }

    [Test]
    public async Task HandlerReturnsBody_DataFrameSentWithEndStream()
    {
        var connection = new TestTcpConnectionContext();
        var bodyText = "Hello, HTTP/2!";

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 200, Body = Encoding.ASCII.GetBytes(bodyText), }
            );

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsFalse();
        await Assert.That(connection.SentFrames.Count).IsEqualTo(2);

        var sentHeaders = connection.SentFrames[0];
        var headersDecoded = DecodeHeadersFrame(sentHeaders, out var respHeaders, out var hdrFlags);

        await Assert.That(headersDecoded).IsTrue();
        await Assert.That(hdrFlags.HasFlag(Http2FrameFlags.EndHeaders)).IsTrue();
        await Assert.That(hdrFlags.HasFlag(Http2FrameFlags.EndStream)).IsFalse();

        var dataFrameBytes = connection.SentFrames[1];
        var dataParsed = TryReadFrame(dataFrameBytes, out var dataFrame);

        await Assert.That(dataParsed).IsTrue();
        await Assert.That(dataFrame!.Type).IsEqualTo(Http2FrameType.Data);
        await Assert.That(dataFrame.StreamId).IsEqualTo(1);
        await Assert.That(dataFrame.HasFlag(Http2FrameFlags.EndStream)).IsTrue();
        await Assert.That(dataFrame.Length).IsEqualTo(bodyText.Length);
        await Assert.That(Encoding.ASCII.GetString(dataFrame.Payload.Span)).IsEqualTo(bodyText);
    }

    [Test]
    public async Task HandlerReturnsEmptyBody_HeadersSentWithEndStream()
    {
        var connection = new TestTcpConnectionContext();

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 204, Body = ReadOnlyMemory<byte>.Empty, }
            );

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsFalse();
        await Assert.That(connection.SentFrames.Count).IsEqualTo(1);

        var decoded = DecodeHeadersFrame(connection.SentFrames[0], out var headers, out var flags);

        await Assert.That(decoded).IsTrue();
        await Assert.That(flags.HasFlag(Http2FrameFlags.EndStream)).IsTrue();
        await Assert.That(headers![0].Item1).IsEqualTo(":status");
        await Assert.That(headers[0].Item2).IsEqualTo("204");
    }

    [Test]
    public async Task ResponseHeaders_ConnectionSpecificHeadersAreFiltered()
    {
        var connection = new TestTcpConnectionContext();

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    Headers = new HttpHeaderCollection
                    {
                        { "Content-Type", "text/plain" },
                        { "Connection", "keep-alive" },
                        { "Transfer-Encoding", "chunked" },
                        { "Keep-Alive", "timeout=5" },
                        { "X-Custom", "hello" },
                    },
                }
            );

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        DecodeHeadersFrame(connection.SentFrames[0], out var headers, out _);

        var headerNames = headers!.Select(h => h.Item1.ToLowerInvariant()).ToList();
        await Assert.That(headerNames).Contains("content-type");
        await Assert.That(headerNames).Contains("x-custom");
        await Assert.That(headerNames).DoesNotContain("connection");
        await Assert.That(headerNames).DoesNotContain("transfer-encoding");
        await Assert.That(headerNames).DoesNotContain("keep-alive");
    }

    [Test]
    public async Task MissingMethodPseudoHeader_SendsGoAwayAndCloses()
    {
        var connection = new TestTcpConnectionContext();

        var hpack = new byte[] { 0x04, 0x04, 0x2F, 0x66, 0x6F, 0x6F };
        var frame = BuildHeadersFrame(
            hpack,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 });

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsTrue();
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task MissingEndHeadersFlag_SendsGoAwayAndCloses()
    {
        var connection = new TestTcpConnectionContext();

        var frame = BuildHeadersFrame(MinimalHpackPayload, Http2FrameFlags.None);

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 });

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsTrue();
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task InvalidHpackBlock_SendsGoAwayAndCloses()
    {
        var connection = new TestTcpConnectionContext();

        var invalidHpack = new byte[] { 0xFF, 0xFF, 0xFF };
        var frame = BuildHeadersFrame(
            invalidHpack,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 });

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsTrue();
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task WrongStreamId_SendsGoAwayAndCloses()
    {
        var connection = new TestTcpConnectionContext();
        var frame = new Http2Frame
        {
            Type = Http2FrameType.Headers,
            Flags = Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            StreamId = 3,
            Payload = MinimalHpackPayload,
        };

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 });

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsTrue();
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task HandlerThrowsException_Returns500()
    {
        var connection = new TestTcpConnectionContext();

        HttpRequestHandler handler = (req, ct) =>
            throw new InvalidOperationException("test failure");

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsFalse();

        DecodeHeadersFrame(connection.SentFrames[0], out var headers, out _);
        await Assert.That(headers![0].Item1).IsEqualTo(":status");
        await Assert.That(headers[0].Item2).IsEqualTo("500");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static byte[] BuildMinimalHpack(string method, string path)
    {
        var output = new List<byte>();

        switch (method)
        {
            case "GET":
                output.Add(0x82);
                break;
            case "POST":
                output.Add(0x83);
                break;
            default:
                output.Add(0x00);
                EncodeRawString(output, ":method");
                EncodeRawString(output, method);
                break;
        }

        output.Add(0x04);
        EncodeRawString(output, path);

        return output.ToArray();
    }

    private static void EncodeRawString(List<byte> output, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length < 127)
        {
            output.Add((byte)bytes.Length);
        }
        else
        {
            output.Add(0x7F);
            var remaining = bytes.Length - 127;
            while (remaining >= 128)
            {
                output.Add((byte)((remaining & 0x7F) | 0x80));
                remaining >>= 7;
            }
            output.Add((byte)remaining);
        }

        if (bytes.Length > 0)
            output.AddRange(bytes);
    }

    private static Http2Frame BuildHeadersFrame(byte[] hpackPayload, Http2FrameFlags flags)
    {
        var encoded = Http2FrameCodec.EncodeFrame(Http2FrameType.Headers, flags, 1, hpackPayload);

        var buffer = new ReadOnlySequence<byte>(encoded);
        Http2FrameCodec.TryReadFrame(buffer, out var frame, out _);
        return frame!;
    }

    private static bool DecodeHeadersFrame(
        byte[] frameBytes,
        out List<(string, string)>? headers,
        out Http2FrameFlags flags
    )
    {
        headers = null;
        flags = Http2FrameFlags.None;

        if (!TryReadFrame(frameBytes, out var frame))
            return false;

        if (frame.Type != Http2FrameType.Headers)
            return false;

        flags = frame.Flags;
        return HpackDecoder.TryDecode(frame.Payload.Span, out headers);
    }

    private static bool TryReadFrame(byte[] data, out Http2Frame? frame)
    {
        var buffer = new ReadOnlySequence<byte>(data);
        return Http2FrameCodec.TryReadFrame(buffer, out frame, out _);
    }

    private sealed class TestTcpConnectionContext : ITcpConnectionContext
    {
        public long ConnectionId => 1;
        public IPEndPoint RemoteEndPoint => new(IPAddress.Loopback, 12345);
        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.MinValue;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.MinValue;
        public List<byte[]> SentFrames { get; } = new();
        public bool IsClosed { get; private set; }

        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default)
        {
            var bytes = new byte[buffer.Length];
            buffer.CopyTo(bytes);
            SentFrames.Add(bytes);
            return Task.CompletedTask;
        }

        public void Close() => IsClosed = true;
    }
}
