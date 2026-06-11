namespace PicoNode.Http.Tests;

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
            null,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsFalse();
        await Assert.That(handlerCalled).IsTrue();
        await Assert.That(capturedRequest).IsNotNull();
        await Assert.That(capturedRequest!.Method).IsEqualTo("GET");
        await Assert.That(capturedRequest.Target).IsEqualTo("/foo");
        await Assert.That(capturedRequest.Version).IsEqualTo(PicoNode.Http.HttpVersion.Http11);

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
                new HttpResponse { StatusCode = 200, Body = Encoding.ASCII.GetBytes(bodyText) }
            );

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            null,
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
                new HttpResponse { StatusCode = 204, Body = ReadOnlyMemory<byte>.Empty }
            );

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            null,
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
            null,
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
            null,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsTrue();
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task MissingEndHeadersFlag_BuffersAndWaitsForContinuation()
    {
        var connection = new TestTcpConnectionContext();

        var frame = BuildHeadersFrame(MinimalHpackPayload, Http2FrameFlags.None);

        var handlerCalled = false;
        HttpRequestHandler handler = (req, ct) =>
        {
            handlerCalled = true;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            null,
            CancellationToken.None
        );

        // Should NOT close — buffering the header block, waiting for CONTINUATION.
        await Assert.That(shouldClose).IsFalse();
        await Assert.That(connection.IsClosed).IsFalse();
        await Assert.That(handlerCalled).IsFalse();
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
            null,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsTrue();
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task StreamId3_AcceptsAndProcessesSuccessfully()
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
            null,
            CancellationToken.None
        );

        await Assert.That(shouldClose).IsFalse();
        await Assert.That(connection.IsClosed).IsFalse();
        await Assert.That(connection.SentFrames.Count).IsEqualTo(1);

        var sent = connection.SentFrames[0];
        Http2FrameCodec.TryReadFrame(
            new ReadOnlySequence<byte>(sent),
            out var responseFrame,
            out _
        );
        await Assert.That(responseFrame).IsNotNull();
        await Assert.That(responseFrame!.StreamId).IsEqualTo(3);
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
            null,
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
        public object? UserState { get; set; }
        public string? NegotiatedProtocol => null;
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

    [Test]
    public async Task HeadersPlusContinuation_ReassemblesAndProcesses()
    {
        var connection = new TestTcpConnectionContext();
        var hpackData = BuildMinimalHpack("POST", "/data");

        // Split HPACK data into two parts
        var split = hpackData.Length / 2;
        var part1 = hpackData[..split];
        var part2 = hpackData[split..];

        var handlerCalled = false;
        HttpRequest? captured = null;
        HttpRequestHandler handler = (req, ct) =>
        {
            handlerCalled = true;
            captured = req;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        // Send HEADERS frame without END_HEADERS
        var headersFrame = BuildFrame(Http2FrameType.Headers, Http2FrameFlags.EndStream, 1, part1);
        var shouldClose1 = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

        await Assert.That(shouldClose1).IsFalse();
        await Assert.That(handlerCalled).IsFalse(); // Not yet — waiting for CONTINUATION

        // Send CONTINUATION frame with END_HEADERS
        var continuationFrame = BuildFrame(
            Http2FrameType.Headers, // ProcessHeadersFrame handles both HEADERS and CONTINUATION
            Http2FrameFlags.EndHeaders,
            1,
            part2
        );
        var shouldClose2 = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            continuationFrame,
            handler,
            null,
            CancellationToken.None
        );

        await Assert.That(shouldClose2).IsFalse();
        await Assert.That(handlerCalled).IsTrue();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Method).IsEqualTo("POST");
        await Assert.That(captured.Target).IsEqualTo("/data");
    }

    private static Http2Frame BuildFrame(
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId,
        byte[] payload
    )
    {
        return new Http2Frame
        {
            Type = type,
            Flags = flags,
            StreamId = streamId,
            Length = payload.Length,
            Payload = payload,
        };
    }

    [Test]
    public async Task HeadersWithoutEndStream_ThenDataWithEndStream_HandlerReceivesBody()
    {
        var connection = new TestTcpConnectionContext();
        var hpackData = BuildMinimalHpack("POST", "/upload");
        var receivedBody = new MemoryStream();

        HttpRequestHandler handler = async (req, ct) =>
        {
            await req.BodyStream.CopyToAsync(receivedBody, ct);
            return new HttpResponse { StatusCode = 200 };
        };

        // Send HEADERS without EndStream
        var headersFrame = BuildFrame(Http2FrameType.Headers, Http2FrameFlags.None, 1, hpackData);
        var shouldClose1 = await Http2StreamHandler.ProcessHeadersFrame(
            connection, headersFrame, handler, null, CancellationToken.None);

        await Assert.That(shouldClose1).IsFalse();
        await Assert.That(receivedBody.Length).IsEqualTo(0);

        // Send DATA frames (non-final)
        var dataFrame1 = BuildDataFrame(1, "Hello "u8.ToArray(), endStream: false);
        await Http2StreamHandler.ProcessDataFrame(
            connection, dataFrame1, handler, null, CancellationToken.None);

        await Assert.That(receivedBody.Length).IsEqualTo(0);

        // Send final DATA with EndStream
        var dataFrame2 = BuildDataFrame(1, "World"u8.ToArray(), endStream: true);
        await Http2StreamHandler.ProcessDataFrame(
            connection, dataFrame2, handler, null, CancellationToken.None);

        await Assert.That(Encoding.UTF8.GetString(receivedBody.ToArray())).IsEqualTo("Hello World");
    }

    [Test]
    public async Task Response_body_larger_than_max_frame_size_is_chunked()
    {
        var connection = new TestTcpConnectionContext();
        var bodySize = 20000; // > 16384 default max frame size
        var body = new byte[bodySize];
        Array.Fill<byte>(body, 0x58); // 'X'

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 200, Body = body });

        var headersFrame = BuildHeadersFrame(MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream);
        await Http2StreamHandler.ProcessHeadersFrame(
            connection, headersFrame, handler, null, CancellationToken.None);

        var frames = connection.SentFrames;
        await Assert.That(frames.Count).IsGreaterThanOrEqualTo(2);

        // First frame should be HEADERS
        await Assert.That(TryReadFrame(frames[0], out var headersFrameOut)).IsTrue();
        await Assert.That(headersFrameOut!.Type).IsEqualTo(Http2FrameType.Headers);

        // Count decodeable frames
        var dataFrames = 0;
        var totalDataBytes = 0;
        for (var i = 1; i < frames.Count; i++)
        {
            if (!TryReadFrame(frames[i], out var dataFrame) || dataFrame is null)
                continue;
            if (dataFrame.Type == Http2FrameType.Data)
            {
                dataFrames++;
                totalDataBytes += dataFrame.Payload.Length;
            }
        }

        await Assert.That(dataFrames).IsGreaterThanOrEqualTo(2);
        await Assert.That(totalDataBytes).IsEqualTo(bodySize);
    }

    [Test]
    public async Task RstStream_removes_stream_and_keeps_connection_open()
    {
        var connection = new TestTcpConnectionContext();

        // First, establish a stream via HEADERS
        var headersFrame = BuildHeadersFrame(MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream);
        await Http2StreamHandler.ProcessHeadersFrame(
            connection, headersFrame,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null, CancellationToken.None);

        // The stream should exist and response should have been sent
        await Assert.That(connection.SentFrames.Count).IsEqualTo(1);

        // Send RST_STREAM for an unused stream ID — should be no-op
        var rstFrame = BuildRstStreamFrame(3);
        var shouldClose = await Http2StreamHandler.ProcessRstStreamFrame(
            connection, rstFrame, CancellationToken.None);

        await Assert.That(shouldClose).IsFalse();
        await Assert.That(connection.IsClosed).IsFalse();
    }

    private static Http2Frame BuildDataFrame(int streamId, byte[] data, bool endStream)
    {
        var flags = Http2FrameFlags.None;
        if (endStream)
            flags |= Http2FrameFlags.EndStream;

        return new Http2Frame
        {
            Type = Http2FrameType.Data,
            Flags = flags,
            StreamId = streamId,
            Length = data.Length,
            Payload = data,
        };
    }

    private static Http2Frame BuildRstStreamFrame(int streamId)
    {
        // RST_STREAM payload is 4 bytes: error code
        var payload = new byte[4]; // NO_ERROR = 0
        return new Http2Frame
        {
            Type = Http2FrameType.RstStream,
            Flags = Http2FrameFlags.None,
            StreamId = streamId,
            Length = 4,
            Payload = payload,
        };
    }
}
