using System.IO.Pipelines;

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
        await Assert.That(capturedRequest.Version).IsEqualTo(PicoNode.Http.HttpVersion.Http2);

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

        await AwaitStreamPumpsAsync(connection);

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
    public async Task MissingMethodPseudoHeader_SendsRstStream()
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

        // Missing pseudo-headers is a stream-level error — connection stays open.
        await Assert.That(shouldClose).IsFalse();
        await Assert.That(connection.IsClosed).IsFalse();
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
        out List<(string, string)> headers,
        out Http2FrameFlags flags
    )
    {
        headers = new List<(string, string)>();
        flags = Http2FrameFlags.None;

        if (!TryReadFrame(frameBytes, out var frame))
            return false;

        if (frame!.Type != Http2FrameType.Headers)
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
        private readonly object _sendGate = new();
        private readonly List<byte[]> _sentFrames = new();

        public long ConnectionId => 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 12345);
        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.MinValue;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.MinValue;
        public object? UserState { get; set; }
        public string? NegotiatedProtocol => null;

        // Snapshot accessor: response pumps write DATA frames from background
        // tasks, so enumeration must not race with frame recording.
        public List<byte[]> SentFrames
        {
            get
            {
                lock (_sendGate)
                {
                    return _sentFrames.ToList();
                }
            }
        }

        public bool IsClosed { get; private set; }

        public Task SendAsync(ReadOnlySequence<byte> buffer, CancellationToken ct = default)
        {
            var bytes = new byte[buffer.Length];
            buffer.CopyTo(bytes);
            lock (_sendGate)
            {
                _sentFrames.Add(bytes);
            }

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
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

        await Assert.That(shouldClose1).IsFalse();
        await Assert.That(receivedBody.Length).IsEqualTo(0);

        // Send DATA frames (non-final)
        var dataFrame1 = BuildDataFrame(1, "Hello "u8.ToArray(), endStream: false);
        await Http2StreamHandler.ProcessDataFrame(
            connection,
            dataFrame1,
            handler,
            null,
            CancellationToken.None
        );

        await Assert.That(receivedBody.Length).IsEqualTo(0);

        // Send final DATA with EndStream
        var dataFrame2 = BuildDataFrame(1, "World"u8.ToArray(), endStream: true);
        await Http2StreamHandler.ProcessDataFrame(
            connection,
            dataFrame2,
            handler,
            null,
            CancellationToken.None
        );

        await Assert.That(Encoding.UTF8.GetString(receivedBody.ToArray())).IsEqualTo("Hello World");
    }

    [Test]
    public async Task DataFrame_decrements_receive_window_and_sends_WINDOW_UPDATE()
    {
        var connection = new TestTcpConnectionContext();
        var runtimeState = new ConnectionRuntimeState();
        connection.UserState = runtimeState;

        // Create stream 1 via HEADERS (no EndStream — body follows in DATA)
        var headersFrame = BuildFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders,
            1,
            MinimalHpackPayload
        );
        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            static (_, _) => default,
            null,
            CancellationToken.None
        );

        // Send DATA frames totalling > 65535 (initial receive window).
        // After ~half the window is consumed, a WINDOW_UPDATE must be sent.
        var chunkSize = 16384;
        var chunk = new byte[chunkSize];
        for (var i = 0; i < 5; i++)
        {
            var dataFrame = BuildDataFrame(1, chunk, endStream: false);
            await Http2StreamHandler.ProcessDataFrame(
                connection,
                dataFrame,
                static (_, _) => default,
                null,
                CancellationToken.None
            );
        }

        // At least one WINDOW_UPDATE frame must have been sent.
        var hasWindowUpdate = false;
        foreach (var sent in connection.SentFrames)
        {
            if (TryReadFrame(sent, out var frame) && frame is not null)
            {
                if (frame.Type == Http2FrameType.WindowUpdate)
                    hasWindowUpdate = true;
            }
        }

        await Assert.That(hasWindowUpdate).IsTrue();
    }

    [Test]
    public async Task Streaming_response_resumes_after_flow_control_backpressure()
    {
        var connection = new TestTcpConnectionContext();
        var runtimeState = new ConnectionRuntimeState
        {
            RemoteInitialWindowSize = 100,
            ConnectionSendWindow = 100,
        };
        connection.UserState = runtimeState;

        // BodyStream larger than both windows — triggers backpressure.
        var bodyData = new byte[500];
        Array.Fill<byte>(bodyData, 0x41);

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    BodyStream = new MemoryStream(bodyData, writable: false),
                }
            );

        var headersFrame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

        // After initial send, some DATA should be sent but no EndStream yet
        // (streaming paused by flow control).
        var hasEndStreamBefore = false;
        foreach (var sent in connection.SentFrames)
        {
            if (
                TryReadFrame(sent, out var f)
                && f is not null
                && f.Type == Http2FrameType.Data
                && f.HasFlag(Http2FrameFlags.EndStream)
            )
            {
                hasEndStreamBefore = true;
            }
        }
        await Assert.That(hasEndStreamBefore).IsFalse();

        // Credit both windows via WINDOW_UPDATE.
        var connWu = BuildFrame(
            Http2FrameType.WindowUpdate,
            Http2FrameFlags.None,
            0,
            BuildWindowUpdatePayload(500)
        );
        await Http2StreamHandler.ProcessWindowUpdateFrame(
            connection,
            connWu,
            CancellationToken.None
        );

        var streamWu = BuildFrame(
            Http2FrameType.WindowUpdate,
            Http2FrameFlags.None,
            1,
            BuildWindowUpdatePayload(500)
        );
        await Http2StreamHandler.ProcessWindowUpdateFrame(
            connection,
            streamWu,
            CancellationToken.None
        );

        // The pump resumes on the background task once the windows are credited.
        await AwaitStreamPumpsAsync(connection);

        // Now all data should be sent including EndStream.
        var hasEndStreamAfter = false;
        var totalDataSent = 0;
        foreach (var sent in connection.SentFrames)
        {
            if (TryReadFrame(sent, out var f) && f is not null && f.Type == Http2FrameType.Data)
            {
                totalDataSent += f.Length;
                if (f.HasFlag(Http2FrameFlags.EndStream))
                    hasEndStreamAfter = true;
            }
        }
        await Assert.That(hasEndStreamAfter).IsTrue();
        await Assert.That(totalDataSent).IsEqualTo(500);
    }

    private static byte[] BuildWindowUpdatePayload(int increment)
    {
        return
        [
            (byte)((increment >> 24) & 0x7F),
            (byte)((increment >> 16) & 0xFF),
            (byte)((increment >> 8) & 0xFF),
            (byte)(increment & 0xFF),
        ];
    }

    [Test]
    public async Task Response_body_larger_than_max_frame_size_is_chunked()
    {
        var connection = new TestTcpConnectionContext();
        var bodySize = 20000; // > 16384 default max frame size
        var body = new byte[bodySize];
        Array.Fill<byte>(body, 0x58); // 'X'

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body });

        var headersFrame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );
        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

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
        var headersFrame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );
        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // The stream should exist and response should have been sent
        await Assert.That(connection.SentFrames.Count).IsEqualTo(1);

        // Send RST_STREAM for an unused stream ID — should be no-op
        var rstFrame = BuildRstStreamFrame(3);
        var shouldClose = await Http2StreamHandler.ProcessRstStreamFrame(
            connection,
            rstFrame,
            CancellationToken.None
        );

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

    // ── H2 BodyStream tests ─────────────────────────────────

    [Test]
    public async Task Streaming_response_does_not_block_the_frame_loop()
    {
        var connection = new TestTcpConnectionContext();
        connection.UserState = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };

        // SSE-like body: a pipe whose writer is never completed — the producer
        // handler never ends (e.g. a chat turn that ignores cancellation).
        var pipe = new Pipe();
        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 200, BodyStream = pipe.Reader.AsStream() }
            );

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        var processTask = Http2StreamHandler
            .ProcessHeadersFrame(connection, frame, handler, null, CancellationToken.None)
            .AsTask();
        var completed = await Task.WhenAny(processTask, Task.Delay(TimeSpan.FromSeconds(2)));

        await Assert
            .That(ReferenceEquals(completed, processTask))
            .IsTrue()
            .Because(
                "a stalled streaming response must not block the frame loop — "
                    + "other requests on the same HTTP/2 connection must still be served"
            );

        // Cleanup: RST the stream so the background pump stops.
        var rst = BuildFrame(
            Http2FrameType.RstStream,
            Http2FrameFlags.None,
            1,
            new byte[] { 0, 0, 0, 8 }
        );
        await Http2StreamHandler.ProcessRstStreamFrame(connection, rst, CancellationToken.None);
    }

    [Test]
    public async Task RstStream_stops_the_streaming_pump()
    {
        var connection = new TestTcpConnectionContext();
        connection.UserState = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };

        var pipe = new Pipe();
        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 200, BodyStream = pipe.Reader.AsStream() }
            );

        var frame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );
        await Http2StreamHandler
            .ProcessHeadersFrame(connection, frame, handler, null, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        var runtime = (ConnectionRuntimeState)connection.UserState!;
        var pump = runtime.Http2Streams![1].ResponsePumpTask;
        await Assert.That(pump).IsNotNull();

        var rst = BuildFrame(
            Http2FrameType.RstStream,
            Http2FrameFlags.None,
            1,
            new byte[] { 0, 0, 0, 8 }
        );
        await Http2StreamHandler.ProcessRstStreamFrame(connection, rst, CancellationToken.None);

        // The pump must observe the RST cancellation and complete.
        await pump!.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task BodyStream_sends_data_in_DATA_frames()
    {
        var connection = new TestTcpConnectionContext();
        var bodyData = "streamed response body"u8.ToArray();

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 200, BodyStream = new MemoryStream(bodyData) }
            );

        var headersFrame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

        await AwaitStreamPumpsAsync(connection);

        await Assert.That(connection.SentFrames.Count).IsGreaterThanOrEqualTo(2);

        var totalPayloadBytes = 0;
        var frames = connection.SentFrames;
        for (var i = 1; i < frames.Count; i++)
        {
            if (!TryReadFrame(frames[i], out var frame) || frame is null)
                continue;
            await Assert.That(frame.Type).IsEqualTo(Http2FrameType.Data);
            totalPayloadBytes += frame.Payload.Length;
        }

        await Assert.That(totalPayloadBytes).IsEqualTo(bodyData.Length);
    }

    [Test]
    public async Task BodyStream_chunked_by_max_frame_size()
    {
        var connection = new TestTcpConnectionContext();
        var bodySize = 20000;
        var bodyData = new byte[bodySize];
        Array.Fill<byte>(bodyData, (byte)'X');

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 200, BodyStream = new MemoryStream(bodyData) }
            );

        var headersFrame = BuildHeadersFrame(
            MinimalHpackPayload,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );

        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

        await AwaitStreamPumpsAsync(connection);

        var dataFrameCount = 0;
        var totalBytes = 0L;
        for (var i = 1; i < connection.SentFrames.Count; i++)
        {
            if (!TryReadFrame(connection.SentFrames[i], out var frame) || frame is null)
                continue;
            if (frame.Type == Http2FrameType.Data)
            {
                dataFrameCount++;
                totalBytes += frame.Payload.Length;
                await Assert.That(frame.Payload.Length).IsLessThanOrEqualTo(16384);
            }
        }

        await Assert.That(dataFrameCount).IsGreaterThanOrEqualTo(2);
        await Assert.That(totalBytes).IsEqualTo(bodySize);
    }

    [Test]
    public async Task DataBuffer_exceeds_limit_sends_RstStream_EnhanceYourCalm()
    {
        var connection = new TestTcpConnectionContext();
        // Set up connection state with a small request body limit
        connection.UserState = new ConnectionRuntimeState
        {
            Protocol = ConnectionProtocol.Http2,
            MaxRequestBodyBytes = 100,
        };

        var handlerCalled = false;
        HttpRequestHandler handler = (req, ct) =>
        {
            handlerCalled = true;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        var hpackData = BuildMinimalHpack("POST", "/upload");

        // Send HEADERS without EndStream (deferred handler)
        var headersFrame = BuildFrame(Http2FrameType.Headers, Http2FrameFlags.None, 1, hpackData);
        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

        // Send DATA frames that accumulate to exceed the 100-byte limit
        // This frame pushes total over 100 (payload is 60 bytes each)
        var dataFrame = BuildDataFrame(1, new byte[60], endStream: false);
        await Http2StreamHandler.ProcessDataFrame(
            connection,
            dataFrame,
            handler,
            null,
            CancellationToken.None
        );

        await Assert.That(handlerCalled).IsFalse(); // Handler still deferred

        // Send second DATA frame — this should exceed the 100-byte limit
        var dataFrame2 = BuildDataFrame(1, new byte[60], endStream: false);
        await Http2StreamHandler.ProcessDataFrame(
            connection,
            dataFrame2,
            handler,
            null,
            CancellationToken.None
        );

        // Verify RST_STREAM was sent
        await Assert.That(connection.SentFrames.Count).IsGreaterThanOrEqualTo(1);
        var lastFrame = connection.SentFrames[0];
        var buffer = new ReadOnlySequence<byte>(lastFrame);
        var parsed = Http2FrameCodec.TryReadFrame(buffer, out var rstFrame, out _);
        await Assert.That(parsed).IsTrue();
        await Assert.That(rstFrame!.Type).IsEqualTo(Http2FrameType.RstStream);
        await Assert.That(rstFrame.StreamId).IsEqualTo(1);

        // Verify ENHANCE_YOUR_CALM error code (0xB)
        var errorCode =
            (rstFrame.Payload.Span[0] << 24)
            | (rstFrame.Payload.Span[1] << 16)
            | (rstFrame.Payload.Span[2] << 8)
            | rstFrame.Payload.Span[3];
        await Assert.That(errorCode).IsEqualTo((int)Http2ErrorCode.EnhanceYourCalm);

        // Verify the stream was removed from tracking
        var state = (ConnectionRuntimeState)connection.UserState!;
        await Assert.That(state.Http2Streams?.ContainsKey(1)).IsFalse();
    }

    [Test]
    public async Task DataBuffer_under_limit_processes_normally()
    {
        var connection = new TestTcpConnectionContext();
        connection.UserState = new ConnectionRuntimeState
        {
            Protocol = ConnectionProtocol.Http2,
            MaxRequestBodyBytes = 1000,
        };

        var receivedBody = new MemoryStream();
        HttpRequestHandler handler = async (req, ct) =>
        {
            await req.BodyStream.CopyToAsync(receivedBody, ct);
            return new HttpResponse { StatusCode = 200 };
        };

        var hpackData = BuildMinimalHpack("POST", "/upload");

        // HEADERS without EndStream
        var headersFrame = BuildFrame(Http2FrameType.Headers, Http2FrameFlags.None, 1, hpackData);
        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

        // Send DATA frames under the limit
        var dataFrame1 = BuildDataFrame(1, "Hello "u8.ToArray(), endStream: false);
        await Http2StreamHandler.ProcessDataFrame(
            connection,
            dataFrame1,
            handler,
            null,
            CancellationToken.None
        );

        var dataFrame2 = BuildDataFrame(1, "World"u8.ToArray(), endStream: true);
        await Http2StreamHandler.ProcessDataFrame(
            connection,
            dataFrame2,
            handler,
            null,
            CancellationToken.None
        );

        await Assert.That(Encoding.UTF8.GetString(receivedBody.ToArray())).IsEqualTo("Hello World");
    }

    // ── CONTINUATION edge cases ────────────────────────────

    [Test]
    public async Task Continuation_empty_payload_with_EndHeaders_no_handler_call()
    {
        var connection = new TestTcpConnectionContext();

        // HEADERS with no HPACK payload and EndHeaders — empty header block
        var headersFrame = BuildFrame(Http2FrameType.Headers, Http2FrameFlags.EndHeaders, 1, []);

        var handlerCalled = false;
        HttpRequestHandler handler = (req, ct) =>
        {
            handlerCalled = true;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );

        // Empty HPACK block: no valid headers to decode, handler should not be called
        await Assert.That(handlerCalled).IsFalse();
    }

    [Test]
    public async Task Continuation_exceeding_MaxHeaderListSize_sends_GoAway()
    {
        var connection = new TestTcpConnectionContext();

        // Build a large HPACK payload that exceeds 16KB MaxHeaderListSize
        // Use BuildFrame directly to avoid Http2FrameCodec's max frame size check
        var largeValue = new string('x', Http2StreamState.MaxHeaderListSize);
        var largeHpack = BuildHpackWithLargeValue(largeValue);

        var frame = BuildFrame(Http2FrameType.Headers, Http2FrameFlags.EndHeaders, 1, largeHpack);

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 });

        var shouldClose = await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            handler,
            null,
            CancellationToken.None
        );

        // Exceeding MaxHeaderListSize is a connection error
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task Continuation_multiple_continuations_no_handler_until_end()
    {
        var connection = new TestTcpConnectionContext();
        var hpackData = BuildMinimalHpack("POST", "/multi");

        // First part: HEADERS without EndHeaders
        var part1 = hpackData[..3];
        var part2 = hpackData[3..];

        var handlerCalled = false;
        HttpRequestHandler handler = (req, ct) =>
        {
            handlerCalled = true;
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

        // HEADERS without EndHeaders
        var headersFrame = BuildFrame(Http2FrameType.Headers, Http2FrameFlags.None, 1, part1);
        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            headersFrame,
            handler,
            null,
            CancellationToken.None
        );
        await Assert.That(handlerCalled).IsFalse();

        // Continue tracking is set on connection state
        var state = connection.UserState as ConnectionRuntimeState;
        await Assert.That(state).IsNotNull();

        // CONTINUATION with EndHeaders + EndStream
        var contFrame = BuildFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            part2
        );
        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            contFrame,
            handler,
            null,
            CancellationToken.None
        );

        await Assert.That(connection.IsClosed).IsFalse();
        await Assert.That(handlerCalled).IsTrue();
    }

    private static byte[] BuildHpackWithLargeValue(string value)
    {
        // Build a minimal HPACK block: literal header with a very large value
        // Format: 0x00 (name index 0, literal) + len(name) + name + len(value) + value
        // Use a simple: literal-with-name-reference encoding
        // First encode the name as literal (index 0)
        var nameBytes = "x-large"u8.ToArray();
        var valueBytes = Encoding.ASCII.GetBytes(value);

        var result = new List<byte>();
        result.Add(0x00); // Literal, new name, incremental indexing

        // Encode name length
        result.Add((byte)nameBytes.Length);
        result.AddRange(nameBytes);

        // Encode value length (may need multi-byte encoding)
        if (valueBytes.Length < 127)
        {
            result.Add((byte)valueBytes.Length);
        }
        else
        {
            result.Add(0x7F);
            var remaining = valueBytes.Length - 127;
            while (remaining >= 128)
            {
                result.Add((byte)((remaining & 0x7F) | 0x80));
                remaining >>= 7;
            }
            result.Add((byte)remaining);
        }

        result.AddRange(valueBytes);
        return result.ToArray();
    }

    [Test]
    public async Task Peer_settings_are_applied_immediately_on_receipt()
    {
        // A SETTINGS frame carrying INITIAL_WINDOW_SIZE=131072 arrives.
        var settings = Http2FrameCodec.EncodeSettings(
            new Http2Setting(Http2SettingId.InitialWindowSize, 131072)
        );
        var connection = new TestTcpConnectionContext();
        var state = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };
        connection.UserState = state;

        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(settings),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // Settings must be applied immediately — not deferred until an ACK.
        await Assert.That(state.RemoteInitialWindowSize).IsEqualTo(131072);
    }

    [Test]
    public async Task Initial_window_size_change_adjusts_existing_stream_send_windows()
    {
        var state = new ConnectionRuntimeState();
        var stream = state.GetOrCreateStream(1)!;
        stream.SendWindow = 65535;

        state.ApplySettings([new Http2Setting(Http2SettingId.InitialWindowSize, 131072)]);

        // RFC 7540 §6.9.2: delta applies to ALL existing streams.
        await Assert.That(stream.SendWindow).IsEqualTo(131072);
    }

    [Test]
    public async Task Idle_reaper_keeps_streams_with_pending_response_work()
    {
        var state = new ConnectionRuntimeState();
        var active = state.GetOrCreateStream(1)!;
        active.ResponseBodyStream = new MemoryStream([1, 2, 3]); // stalled on flow control
        active.LastActivityUtc = DateTime.UtcNow.AddMinutes(-10);

        var done = state.GetOrCreateStream(3)!;
        done.ResponseSent = true;
        done.EndStreamReceived = true;
        done.LastActivityUtc = DateTime.UtcNow.AddMinutes(-10); // idle AND complete

        state.RemoveIdleStreams();

        await Assert
            .That(state.Http2Streams!.ContainsKey(1))
            .IsTrue()
            .Because("a stream with pending response work must not be reaped");
        await Assert
            .That(state.Http2Streams.ContainsKey(3))
            .IsFalse()
            .Because("completed streams are still collected");
    }

    [Test]
    public async Task Data_frame_exceeding_stream_window_is_rejected()
    {
        var state = new ConnectionRuntimeState();
        var stream = state.GetOrCreateStream(1)!;
        // DATA is only legal after HEADERS — move the stream Idle → Open first,
        // otherwise the state machine rejects the frame before the flow-control check.
        stream.StateMachine.TryTransition(Http2StreamStateMachine.Trigger.Headers, out _);
        stream.ReceiveWindow = 10;

        var frame = BuildFrame(Http2FrameType.Data, Http2FrameFlags.EndStream, 1, new byte[100]);
        var connection = new TestTcpConnectionContext();
        connection.UserState = state;

        var result = await Http2StreamHandler.ProcessDataFrame(
            connection,
            frame,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // A RST_STREAM with FLOW_CONTROL_ERROR must have been sent (frame type 3).
        var sentRst = connection.SentFrames.Any(f =>
            f.Length >= 13 && f[3] == (byte)Http2FrameType.RstStream
        );
        await Assert
            .That(sentRst)
            .IsTrue()
            .Because("DATA exceeding the stream receive window must be rejected with RST_STREAM");
    }

    [Test]
    public async Task Zero_increment_stream_window_update_is_rejected()
    {
        var state = new ConnectionRuntimeState();
        state.GetOrCreateStream(5);
        var connection = new TestTcpConnectionContext();
        connection.UserState = state;

        var frame = BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 5, new byte[4]);

        await Http2StreamHandler.ProcessWindowUpdateFrame(
            connection,
            frame,
            CancellationToken.None
        );

        var sentRst = connection.SentFrames.Any(f =>
            f.Length >= 13 && f[3] == (byte)Http2FrameType.RstStream
        );
        await Assert
            .That(sentRst)
            .IsTrue()
            .Because(
                "stream-level WINDOW_UPDATE with increment 0 is a stream error (RFC 7540 §6.9)"
            );
    }

    [Test]
    public async Task Incoming_streams_are_limited_by_our_advertised_concurrency()
    {
        var state = new ConnectionRuntimeState();
        state.RemoteMaxConcurrentStreams = 1000; // peer's value must NOT govern our limit
        for (var i = 1; i <= 199; i += 2) // odd stream IDs only (even IDs are invalid)
            state.GetOrCreateStream(i); // → exactly 100 tracked streams

        var frame = BuildFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            201,
            [0x82, 0x86, 0x44, 0x01, (byte)'/'] // minimal GET / HPACK
        );
        var connection = new TestTcpConnectionContext();
        connection.UserState = state;

        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // A RST_STREAM with REFUSED_STREAM must have been sent (frame type 3).
        var sentRst = connection.SentFrames.Any(f =>
            f.Length >= 13 && f[3] == (byte)Http2FrameType.RstStream
        );
        await Assert
            .That(sentRst)
            .IsTrue()
            .Because("streams beyond our advertised limit (100) must be refused");
    }

    [Test]
    public async Task ApplySettings_HeaderTableSize_ResizesEncoderTableNotDecoderTable()
    {
        var state = new ConnectionRuntimeState();

        state.ApplySettings([new Http2Setting(Http2SettingId.HeaderTableSize, 1024)]);

        // Decoder table capacity is OUR advertised value (4096) — the peer's
        // SETTINGS_HEADER_TABLE_SIZE only limits what WE may index (encoder).
        await Assert.That(state.HpackTable.Capacity).IsEqualTo(4096);
        await Assert.That(state.ResponseHpackEncoder.DynamicTable.Capacity).IsEqualTo(1024);

        // 0 must disable the encoder dynamic table (RFC 7541 §4.2).
        state.ApplySettings([new Http2Setting(Http2SettingId.HeaderTableSize, 0)]);
        await Assert.That(state.ResponseHpackEncoder.DynamicTable.Capacity).IsEqualTo(0);
        await Assert
            .That(state.HpackTable.Capacity)
            .IsEqualTo(4096)
            .Because("the peer cannot change our decoder table capacity");
    }

    [Test]
    public async Task MultipleResponses_SharedEncoderTable_DecodeRoundTrips()
    {
        // Regression guard for ERR_HTTP2_COMPRESSION_ERROR: response encoding
        // must use the connection's shared encoder table and stay in sync with
        // a peer decoder that mirrors the same table across requests.
        var connection = new TestTcpConnectionContext();
        connection.UserState = new ConnectionRuntimeState { Protocol = ConnectionProtocol.Http2 };

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    Headers = new HttpHeaderCollection
                    {
                        { "x-custom", req.Path == "/one" ? "alpha" : "beta" },
                        { "x-custom-2", "shared-value" },
                    },
                }
            );

        var peerTable = new HpackDynamicTable();

        for (var i = 0; i < 2; i++)
        {
            var hpack = BuildMinimalHpack("GET", i == 0 ? "/one" : "/two");
            var frame = BuildHeadersFrame(
                hpack,
                Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
            );

            await Http2StreamHandler.ProcessHeadersFrame(
                connection,
                frame,
                handler,
                null,
                CancellationToken.None
            );

            var sentHeaders = connection.SentFrames[^1];
            var ok = DecodeHeadersFrameWithTable(sentHeaders, out var headers, out _, peerTable);

            await Assert
                .That(ok)
                .IsTrue()
                .Because("response HPACK must stay in sync with the peer decoder table");
            await Assert.That(headers!.Any(h => h.Item1 == ":status")).IsTrue();
        }
    }

    private static async Task AwaitStreamPumpsAsync(TestTcpConnectionContext connection)
    {
        var runtime = connection.UserState as ConnectionRuntimeState;
        if (runtime?.Http2Streams is null)
            return;

        // Response pumps clear their own task reference on completion; collect
        // the current tasks first, then await each one.
        var pumps = runtime
            .Http2Streams.Values.Where(s => s.ResponsePumpTask is not null)
            .Select(s => s.ResponsePumpTask!)
            .ToList();
        foreach (var pump in pumps)
        {
            await pump;
        }
    }

    private static bool DecodeHeadersFrameWithTable(
        byte[] frameBytes,
        out List<(string, string)> headers,
        out Http2FrameFlags flags,
        HpackDynamicTable table
    )
    {
        headers = new List<(string, string)>();
        flags = Http2FrameFlags.None;

        if (!TryReadFrame(frameBytes, out var frame))
            return false;

        if (frame!.Type != Http2FrameType.Headers)
            return false;

        flags = frame.Flags;
        return HpackDecoder.TryDecode(frame.Payload.Span, out headers, table);
    }

    [Test]
    public async Task Window_update_overflowing_stream_window_sends_flow_control_error()
    {
        var state = new ConnectionRuntimeState();
        var stream = state.GetOrCreateStream(5)!;
        stream.SendWindow = int.MaxValue - 10;
        var connection = new TestTcpConnectionContext();
        connection.UserState = state;

        var frame = BuildFrame(
            Http2FrameType.WindowUpdate,
            Http2FrameFlags.None,
            5,
            BuildWindowUpdatePayload(0x7FFFFFFF)
        );

        await Http2StreamHandler.ProcessWindowUpdateFrame(
            connection,
            frame,
            CancellationToken.None
        );

        // RFC 7540 §6.9: exceeding 2^31-1 must be a FLOW_CONTROL_ERROR.
        var sentRst = connection.SentFrames.FirstOrDefault(f =>
            f.Length >= 13
            && f[3] == (byte)Http2FrameType.RstStream
            && f[9] == 0
            && f[10] == 0
            && f[11] == 0
            && f[12] == 3 // FLOW_CONTROL_ERROR
        );
        await Assert
            .That(sentRst)
            .IsNotNull()
            .Because(
                "a WINDOW_UPDATE overflowing the stream send window must send RST_STREAM FLOW_CONTROL_ERROR"
            );
    }

    [Test]
    public async Task Window_update_overflowing_connection_window_sends_goaway()
    {
        var state = new ConnectionRuntimeState();
        state.ConnectionSendWindow = int.MaxValue - 10;
        var connection = new TestTcpConnectionContext();
        connection.UserState = state;

        var frame = BuildFrame(
            Http2FrameType.WindowUpdate,
            Http2FrameFlags.None,
            0,
            BuildWindowUpdatePayload(0x7FFFFFFF)
        );

        var shouldClose = await Http2StreamHandler.ProcessWindowUpdateFrame(
            connection,
            frame,
            CancellationToken.None
        );

        await Assert
            .That(shouldClose)
            .IsTrue()
            .Because("overflowing the connection window is a connection error");
        await Assert.That(connection.IsClosed).IsTrue();
        var goAway = connection.SentFrames.FirstOrDefault(f =>
            f.Length >= 17 && f[3] == (byte)Http2FrameType.GoAway && f[16] == 3
        );
        await Assert.That(goAway).IsNotNull().Because("GOAWAY must carry FLOW_CONTROL_ERROR");
    }

    [Test]
    public async Task Settings_initial_window_size_above_limit_is_connection_error()
    {
        var settings = Http2FrameCodec.EncodeSettings(
            new Http2Setting(Http2SettingId.InitialWindowSize, 0x80000000u)
        );
        var connection = new TestTcpConnectionContext();

        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(settings),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.IsClosed).IsTrue();
        var goAway = connection.SentFrames.FirstOrDefault(f =>
            f.Length >= 17 && f[3] == (byte)Http2FrameType.GoAway && f[16] == 3
        );
        await Assert
            .That(goAway)
            .IsNotNull()
            .Because("INITIAL_WINDOW_SIZE above 2^31-1 is a FLOW_CONTROL_ERROR (RFC 7540 §6.9.2)");
    }

    [Test]
    public async Task Large_response_headers_are_split_across_continuation_frames()
    {
        var connection = new TestTcpConnectionContext();
        connection.UserState = new ConnectionRuntimeState
        {
            Protocol = ConnectionProtocol.Http2,
            RemoteMaxFrameSize = 16384,
        };
        var bigValue = new string('a', 40_000);

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    Headers = new HttpHeaderCollection { { "x-big", bigValue } },
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

        // Reassemble the header block across HEADERS + CONTINUATION frames.
        using var headerBlock = new MemoryStream();
        var sawContinuation = false;
        var endHeadersSeen = false;
        foreach (var sent in connection.SentFrames)
        {
            if (!TryReadFrame(sent, out var sentFrame))
                continue;
            if (sentFrame!.Type == Http2FrameType.Headers)
            {
                headerBlock.Write(sentFrame.Payload.Span);
                endHeadersSeen = sentFrame.HasFlag(Http2FrameFlags.EndHeaders);
            }
            else if (sentFrame.Type == Http2FrameType.Continuation)
            {
                sawContinuation = true;
                await Assert
                    .That(sentFrame.StreamId)
                    .IsEqualTo(1)
                    .Because("CONTINUATION must target the same stream");
                headerBlock.Write(sentFrame.Payload.Span);
                endHeadersSeen = sentFrame.HasFlag(Http2FrameFlags.EndHeaders);
            }
        }

        await Assert
            .That(sawContinuation)
            .IsTrue()
            .Because("a 40 KB header block must be split when the peer max frame size is 16 KB");
        await Assert.That(endHeadersSeen).IsTrue();

        var decoded = HpackDecoder.TryDecode(headerBlock.ToArray(), out var headers);
        await Assert.That(decoded).IsTrue();
        await Assert.That(headers!.Any(h => h.Item1 == "x-big" && h.Item2 == bigValue)).IsTrue();
    }

    [Test]
    public async Task Push_promise_frame_is_connection_error()
    {
        var connection = new TestTcpConnectionContext();
        connection.UserState = new ConnectionRuntimeState { ReceivedPostPrefaceFrame = true };
        var frame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.PushPromise,
            Http2FrameFlags.None,
            2,
            new byte[4]
        );

        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.IsClosed).IsTrue();
        var goAway = connection.SentFrames.FirstOrDefault(f =>
            f.Length >= 17 && f[3] == (byte)Http2FrameType.GoAway && f[16] == 1
        );
        await Assert
            .That(goAway)
            .IsNotNull()
            .Because("a client PUSH_PROMISE is a PROTOCOL_ERROR, not HTTP_1_1_REQUIRED");
    }

    [Test]
    public async Task Unknown_frame_type_is_ignored()
    {
        var connection = new TestTcpConnectionContext();
        connection.UserState = new ConnectionRuntimeState { ReceivedPostPrefaceFrame = true };
        var frame = Http2FrameCodec.EncodeFrame(
            (Http2FrameType)0x0B,
            Http2FrameFlags.None,
            0,
            new byte[4]
        );

        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // RFC 7540 §5.5: unknown frame types MUST be ignored.
        await Assert.That(connection.IsClosed).IsFalse();
        await Assert.That(connection.SentFrames).IsEmpty();
    }

    [Test]
    public async Task Settings_payload_not_multiple_of_six_is_frame_size_error()
    {
        var connection = new TestTcpConnectionContext();
        var frame = Http2FrameCodec.EncodeFrame(
            Http2FrameType.Settings,
            Http2FrameFlags.None,
            0,
            new byte[5]
        );

        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.IsClosed).IsTrue();
        var goAway = connection.SentFrames.FirstOrDefault(f =>
            f.Length >= 17 && f[3] == (byte)Http2FrameType.GoAway && f[16] == 6
        );
        await Assert
            .That(goAway)
            .IsNotNull()
            .Because("SETTINGS payload must be a multiple of 6 bytes (FRAME_SIZE_ERROR)");
    }

    [Test]
    public async Task Rst_stream_wrong_length_is_frame_size_error()
    {
        var state = new ConnectionRuntimeState();
        state.GetOrCreateStream(3);
        var connection = new TestTcpConnectionContext();
        connection.UserState = state;
        var frame = BuildFrame(Http2FrameType.RstStream, Http2FrameFlags.None, 3, new byte[2]);

        var shouldClose = await Http2StreamHandler.ProcessRstStreamFrame(
            connection,
            frame,
            CancellationToken.None
        );

        await Assert
            .That(shouldClose)
            .IsTrue()
            .Because("RST_STREAM length != 4 is a connection error (RFC 7540 §6.4)");
        await Assert.That(connection.IsClosed).IsTrue();
        var goAway = connection.SentFrames.FirstOrDefault(f =>
            f.Length >= 17 && f[3] == (byte)Http2FrameType.GoAway && f[16] == 6
        );
        await Assert.That(goAway).IsNotNull();
    }

    [Test]
    public async Task Window_update_wrong_length_is_frame_size_error()
    {
        var connection = new TestTcpConnectionContext();
        var frame = BuildFrame(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, new byte[3]);

        var shouldClose = await Http2StreamHandler.ProcessWindowUpdateFrame(
            connection,
            frame,
            CancellationToken.None
        );

        await Assert
            .That(shouldClose)
            .IsTrue()
            .Because("WINDOW_UPDATE length != 4 is a connection error (RFC 7540 §6.9)");
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task Data_frame_on_stream_zero_is_connection_error()
    {
        var connection = new TestTcpConnectionContext();
        connection.UserState = new ConnectionRuntimeState { ReceivedPostPrefaceFrame = true };
        var frame = BuildFrame(Http2FrameType.Data, Http2FrameFlags.None, 0, new byte[4]);

        var shouldClose = await Http2StreamHandler.ProcessDataFrame(
            connection,
            frame,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert
            .That(shouldClose)
            .IsTrue()
            .Because("DATA on stream 0 is a connection error (RFC 7540 §6.1)");
        await Assert.That(connection.IsClosed).IsTrue();
        var goAway = connection.SentFrames.FirstOrDefault(f =>
            f.Length >= 17 && f[3] == (byte)Http2FrameType.GoAway && f[16] == 1
        );
        await Assert.That(goAway).IsNotNull().Because("GOAWAY must carry PROTOCOL_ERROR");
    }

    [Test]
    public async Task Path_pseudo_header_with_invalid_percent_encoding_is_rejected()
    {
        // HPACK: indexed :method GET (0x82), literal-without-indexing :path
        // (name index 4 → 0x04) with value "/foo%zz" (7 bytes, invalid percent escape).
        byte[] hpack =
        [
            0x82,
            0x04,
            0x07,
            (byte)'/',
            (byte)'f',
            (byte)'o',
            (byte)'o',
            (byte)'%',
            (byte)'z',
            (byte)'z',
        ];
        var frame = BuildHeadersFrame(
            hpack,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream
        );
        var connection = new TestTcpConnectionContext();

        await Http2StreamHandler.ProcessHeadersFrame(
            connection,
            frame,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        var sentRst = connection.SentFrames.Any(f =>
            f.Length >= 13 && f[3] == (byte)Http2FrameType.RstStream
        );
        await Assert
            .That(sentRst)
            .IsTrue()
            .Because("H2 :path must validate %-escapes like the HTTP/1.1 request line parser does");
    }
}
