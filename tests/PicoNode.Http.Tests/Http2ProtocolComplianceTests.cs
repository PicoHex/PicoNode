using System.IO.Pipelines;
using PicoNode.Http;
using PicoNode.Http.Internal.ConnectionRuntime;

namespace PicoNode.Http.Tests;

/// <summary>
/// Protocol-compliance regression tests mirroring the h2spec suite
/// (https://github.com/summerwind/h2spec, v2.6.0) that PicoNode.Http must
/// pass. Each test mirrors one h2spec test case's frame sequence and expected
/// response at the Http2StreamHandler / Http2ConnectionProcessor level.
/// </summary>
public sealed class Http2ProtocolComplianceTests
{
    // ── 6.9.2 Initial Flow-Control Window Size ─────────────────────

    [Test]
    public async Task Buffered_body_respects_stream_send_window_and_resumes_on_window_update()
    {
        var connection = new ComplianceTestContext();
        var runtimeState = new ConnectionRuntimeState
        {
            RemoteInitialWindowSize = 100,
            ConnectionSendWindow = 100,
            ReceivedPostPrefaceFrame = true, // preface + SETTINGS already exchanged
        };
        connection.UserState = runtimeState;

        var bodyData = new byte[500];
        Array.Fill<byte>(bodyData, 0x42);

        HttpRequestHandler handler = (req, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = bodyData });

        var frame = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            BuildMinimalHpack("GET", "/foo")
        );

        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            handler,
            null,
            CancellationToken.None
        );

        // Only the window-sized amount may be sent; no EndStream yet. The
        // pump runs on a background task, so poll until it reaches the
        // flow-control wall (exactly 100 bytes) and verify it does not
        // overshoot while stalled.
        await WaitUntilAsync(() => GetDataPayloadBytes(connection) == 100, TimeSpan.FromSeconds(2));
        await Assert.That(connection.HasEndStream(1)).IsFalse();
        await Task.Delay(100);
        await Assert.That(GetDataPayloadBytes(connection)).IsEqualTo(100);

        // Credit the windows and let the pump finish.
        var streamWu = BuildFrame(
            Http2FrameType.WindowUpdate,
            Http2FrameFlags.None,
            1,
            BuildWindowUpdatePayload(400)
        );
        await Http2StreamHandler.ProcessWindowUpdateFrame(
            connection,
            streamWu,
            CancellationToken.None
        );
        var connWu = BuildFrame(
            Http2FrameType.WindowUpdate,
            Http2FrameFlags.None,
            0,
            BuildWindowUpdatePayload(400)
        );
        await Http2StreamHandler.ProcessWindowUpdateFrame(
            connection,
            connWu,
            CancellationToken.None
        );

        await AwaitStreamPumpsAsync(connection);

        await Assert.That(GetDataPayloadBytes(connection)).IsEqualTo(500);
        await Assert.That(connection.HasEndStream(1)).IsTrue();
    }

    // ── 5.1 Stream States: idle ────────────────────────────────────

    [Test]
    public async Task Idle_stream_Data_frame_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var frame = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.EndStream,
            1,
            "test"u8.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
        await Assert.That(connection.IsClosed).IsTrue();
    }

    [Test]
    public async Task Idle_stream_RstStream_frame_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var frame = BuildHeadersFrame(
            Http2FrameType.RstStream,
            Http2FrameFlags.None,
            1,
            new byte[] { 0, 0, 0, 8 }
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Idle_stream_WindowUpdate_frame_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var frame = BuildHeadersFrame(
            Http2FrameType.WindowUpdate,
            Http2FrameFlags.None,
            1,
            BuildWindowUpdatePayload(100)
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Idle_stream_Continuation_frame_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var frame = BuildHeadersFrame(
            Http2FrameType.Continuation,
            Http2FrameFlags.EndHeaders,
            1,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    // ── 5.1 Stream States: half-closed (remote) / closed ────────────

    [Test]
    public async Task HalfClosedRemote_Data_frame_triggers_RST_STREAM_CLOSED()
    {
        var connection = NewConnection();
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );

        var data = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.EndStream,
            1,
            "test"u8.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(data),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.StreamClosed);
    }

    [Test]
    public async Task HalfClosedRemote_Headers_frame_triggers_RST_STREAM_CLOSED()
    {
        var connection = NewConnection();
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );

        var second = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            BuildMinimalHpack("GET", "/bar")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(second),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // h2spec's VerifyStreamError(STREAM_CLOSED) accepts either a RST_STREAM
        // or a GOAWAY (the response may or may not have completed first).
        var rstCode = connection.LastRstStreamCode;
        var goAwayCode = connection.LastGoAwayErrorCode;
        var streamClosed =
            rstCode == Http2ErrorCode.StreamClosed || goAwayCode == Http2ErrorCode.StreamClosed;
        await Assert.That(streamClosed).IsTrue();
    }

    [Test]
    public async Task ClosedByRst_stream_frames_trigger_RST_STREAM_CLOSED()
    {
        var connection = NewConnection();
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders, // no EndStream — stream stays open
            1,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        var rst = BuildHeadersFrame(
            Http2FrameType.RstStream,
            Http2FrameFlags.None,
            1,
            new byte[] { 0, 0, 0, 8 }
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(rst),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // DATA after peer RST → RST_STREAM STREAM_CLOSED.
        var data = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.EndStream,
            1,
            "test"u8.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(data),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.StreamClosed);
    }

    // ── 5.1.1 Stream Identifiers ────────────────────────────────────

    [Test]
    public async Task Even_numbered_stream_identifier_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var frame = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            2,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Smaller_stream_identifier_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var first = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            5,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(first),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        var second = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            3,
            BuildMinimalHpack("GET", "/bar")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(second),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    // ── 6.3 PRIORITY ────────────────────────────────────────────────

    [Test]
    public async Task Priority_on_stream_zero_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var frame = BuildHeadersFrame(
            Http2FrameType.Priority,
            Http2FrameFlags.None,
            0,
            new byte[] { 0, 0, 0, 0, 16 }
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(frame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Priority_with_wrong_length_triggers_RST_FRAME_SIZE_ERROR()
    {
        var connection = NewConnection();
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        var priority = BuildHeadersFrame(
            Http2FrameType.Priority,
            Http2FrameFlags.None,
            1,
            new byte[] { 0x80, 0, 0, 1 } // 4 bytes — must be 5
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(priority),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.FrameSizeError);
    }

    [Test]
    public async Task Self_dependent_Priority_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        // StreamDep = 1 (itself), weight 16.
        var priority = BuildHeadersFrame(
            Http2FrameType.Priority,
            Http2FrameFlags.None,
            1,
            new byte[] { 0, 0, 0, 1, 16 }
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(priority),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    // ── 6.10 CONTINUATION ───────────────────────────────────────────

    [Test]
    public async Task Multiple_Continuation_frames_are_accepted()
    {
        var connection = NewConnection();
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndStream, // no EndHeaders
            1,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );

        var cont1 = BuildHeadersFrame(
            Http2FrameType.Continuation,
            Http2FrameFlags.None,
            1,
            [0x00, 0x01, 0x78, 0x01, 0x79]
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(cont1),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );
        Console.WriteLine($"[dbg] after HEADERS: frames={connection.SentFrames.Count}");

        var cont2 = BuildHeadersFrame(
            Http2FrameType.Continuation,
            Http2FrameFlags.EndHeaders,
            1,
            [0x00, 0x01, 0x61, 0x01, 0x62]
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(cont2),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );
        Console.WriteLine($"[dbg] after CONT1: frames={connection.SentFrames.Count}");

        await AwaitStreamPumpsAsync(connection);
        foreach (var f in connection.SentFrames)
        {
            _ = TryReadFrame(f, out var p2);
            var code = "";
            if (p2 is { Payload.Length: >= 8 })
            {
                var o = p2.Payload.Length - 4;
                code =
                    $" code=0x{p2.Payload.Span[o]:x2}{p2.Payload.Span[o + 1]:x2}{p2.Payload.Span[o + 2]:x2}{p2.Payload.Span[o + 3]:x2}";
            }
            Console.WriteLine(
                $"[dbg] type={(p2 is null ? "?" : p2.Type.ToString())} len={f.Length}{code}"
            );
        }
        await Assert.That(connection.HasResponseHeaders(1)).IsTrue();
    }

    [Test]
    public async Task Continuation_after_EndHeaders_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        var cont = BuildHeadersFrame(
            Http2FrameType.Continuation,
            Http2FrameFlags.EndHeaders,
            1,
            [0x00, 0x01, 0x78]
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(cont),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Non_Continuation_frame_mid_header_block_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.None, // no EndHeaders — block pending
            1,
            BuildMinimalHpack("POST", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        var data = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.EndStream,
            1,
            "test"u8.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(data),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    // ── 5.1 Stream States: closed (via END_STREAM) ──────────────────

    [Test]
    public async Task Closed_stream_Data_frame_triggers_RST_STREAM_CLOSED()
    {
        var connection = NewConnection();
        await SendCompleteGetAsync(connection, 1);

        var data = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.EndStream,
            1,
            "test"u8.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(data),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.StreamClosed);
    }

    [Test]
    public async Task Closed_stream_Headers_frame_triggers_GOAWAY_STREAM_CLOSED()
    {
        var connection = NewConnection();
        await SendCompleteGetAsync(connection, 1);

        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            BuildMinimalHpack("GET", "/bar")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.StreamClosed);
    }

    // ── 6.5.2 SETTINGS ENABLE_PUSH ──────────────────────────────────

    [Test]
    public async Task EnablePush_value_other_than_0_or_1_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var settings = BuildHeadersFrame(
            Http2FrameType.Settings,
            Http2FrameFlags.None,
            0,
            new byte[] { 0x00, 0x02, 0x00, 0x00, 0x00, 0x02 } // ENABLE_PUSH = 2
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(settings),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    // ── 8.1.2 HTTP Header Fields ────────────────────────────────────

    [Test]
    public async Task Uppercase_header_field_name_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte>(BuildMinimalHpack("GET", "/foo"));
        AddLiteral(block, "X-Custom", "1");
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Unknown_pseudo_header_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte>(BuildMinimalHpack("GET", "/foo"));
        AddLiteral(block, ":foo", "bar");
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Response_pseudo_header_in_request_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte>(BuildMinimalHpack("GET", "/foo"));
        AddLiteral(block, ":status", "200");
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Empty_path_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte>(BuildMinimalHpack("GET", ""));
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Missing_scheme_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        // HPACK without :scheme (0x86 omitted).
        var block = new List<byte> { 0x82, 0x04, 0x04 };
        block.AddRange("/foo"u8.ToArray());
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Content_length_mismatch_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        // POST with content-length: 0, then a 4-byte DATA frame.
        var block = new List<byte> { 0x83, 0x04, 0x04 };
        block.AddRange("/foo"u8.ToArray());
        block.Add(0x86);
        AddLiteral(block, "content-length", "0");
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        var data = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.EndStream,
            1,
            "test"u8.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(data),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    // ── 8.1 Trailers ────────────────────────────────────────────────

    [Test]
    public async Task Pseudo_header_as_trailers_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        // POST, headers complete, body without EndStream.
        var block = new List<byte> { 0x83, 0x04, 0x04 };
        block.AddRange("/foo"u8.ToArray());
        block.Add(0x86);
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        var data = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.None,
            1,
            "test"u8.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(data),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // Trailers containing a pseudo-header → stream error PROTOCOL_ERROR.
        var trailers = new List<byte>();
        AddLiteral(trailers, ":foo", "bar");
        var trailerFrame = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            trailers.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(trailerFrame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    [Test]
    public async Task Second_Headers_without_EndStream_triggers_RST_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte> { 0x83, 0x04, 0x04 };
        block.AddRange("/foo"u8.ToArray());
        block.Add(0x86);
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // Trailers without END_STREAM → malformed (§8.1).
        var trailers = new List<byte>();
        AddLiteral(trailers, "x-test", "ok");
        var trailerFrame = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders, // no EndStream
            1,
            trailers.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(trailerFrame),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastRstStreamCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    // ── 6.2 / 6.1 Padding ───────────────────────────────────────────

    [Test]
    public async Task Headers_frame_with_padding_is_accepted()
    {
        var connection = NewConnection();
        var block = BuildMinimalHpack("GET", "/foo");
        // PADDED: pad length (1 byte) + block + padding.
        var payload = new List<byte> { 0x05 };
        payload.AddRange(block);
        payload.AddRange(new byte[5]);
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream | Http2FrameFlags.Padded,
            1,
            payload.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );

        await AwaitStreamPumpsAsync(connection);
        await Assert.That(connection.HasResponseHeaders(1)).IsTrue();
    }

    [Test]
    public async Task Data_frame_with_invalid_pad_length_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte> { 0x83, 0x04, 0x04 };
        block.AddRange("/foo"u8.ToArray());
        block.Add(0x86);
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        // PADDED DATA with pad length larger than the payload.
        var data = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.EndStream | Http2FrameFlags.Padded,
            1,
            new byte[] { 0x7F, 0x00 } // pad length 127, only 1 byte of data
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(data),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
    }

    // ── HPACK 5.2 String Literal: Huffman padding ──────────────────

    [Test]
    public async Task Huffman_padding_longer_than_7_bits_triggers_GOAWAY_COMPRESSION_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte>(BuildMinimalHpack("GET", "/foo"));
        // h2spec: Literal without Indexing - New Name (x-test: test) with an
        // extra padding octet at the end.
        block.AddRange(
            new byte[] { 0x00, 0x85, 0xf2, 0xb2, 0x4a, 0x84, 0xff, 0x84, 0x49, 0x50, 0x9f, 0xff }
        );
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert
            .That(connection.LastGoAwayErrorCode)
            .IsEqualTo(Http2ErrorCode.CompressionError);
    }

    [Test]
    public async Task Huffman_string_padded_by_zero_triggers_GOAWAY_COMPRESSION_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte>(BuildMinimalHpack("GET", "/foo"));
        // h2spec: Literal without Indexing - New Name padded by zero.
        block.AddRange(
            new byte[] { 0x00, 0x85, 0xf2, 0xb2, 0x4a, 0x84, 0xff, 0x83, 0x49, 0x50, 0x90 }
        );
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert
            .That(connection.LastGoAwayErrorCode)
            .IsEqualTo(Http2ErrorCode.CompressionError);
    }

    // ── 3.5 Invalid Connection Preface ──────────────────────────────

    [Test]
    public async Task Invalid_connection_preface_triggers_GOAWAY_PROTOCOL_ERROR()
    {
        var connection = new ComplianceTestContext();
        var handler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            }
        );

        var buffer = new ReadOnlySequence<byte>(
            Encoding.ASCII.GetBytes("INVALID CONNECTION PREFACE\r\n\r\n")
        );
        await handler.OnReceivedAsync(connection, buffer, CancellationToken.None);

        await Assert.That(connection.LastGoAwayErrorCode).IsEqualTo(Http2ErrorCode.ProtocolError);
        await Assert.That(connection.IsClosed).IsTrue();
    }

    // ── HPACK 4.2 Dynamic Table Size Update ────────────────────────

    [Test]
    public async Task Multiple_size_updates_at_start_of_header_block_are_accepted()
    {
        var connection = NewConnection();
        // Two consecutive size updates (0x3F 0xE1 0x07 = 1024, then 0x3E = 31-1)
        // at the START of the block, then the request headers.
        var block = new List<byte> { 0x3F, 0xE1, 0x07, 0x3E };
        block.AddRange(BuildMinimalHpack("GET", "/foo"));
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );

        await AwaitStreamPumpsAsync(connection);
        await Assert.That(connection.HasResponseHeaders(1)).IsTrue();
    }

    [Test]
    public async Task Dynamic_table_size_update_at_end_of_header_block_triggers_GOAWAY_COMPRESSION_ERROR()
    {
        var connection = NewConnection();
        var block = new List<byte>(BuildMinimalHpack("GET", "/foo"));
        block.Add(0x20); // size update (prefix 001) at the END of the block
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            null,
            CancellationToken.None
        );

        await Assert
            .That(connection.LastGoAwayErrorCode)
            .IsEqualTo(Http2ErrorCode.CompressionError);
    }

    // ── 8.1 Valid trailers ─────────────────────────────────────────

    [Test]
    public async Task Valid_trailers_complete_the_request_and_get_a_response()
    {
        var connection = NewConnection();
        var block = new List<byte> { 0x83, 0x04, 0x04 };
        block.AddRange("/foo"u8.ToArray());
        block.Add(0x86);
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders,
            1,
            block.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );

        var data = BuildHeadersFrame(
            Http2FrameType.Data,
            Http2FrameFlags.None,
            1,
            "test"u8.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(data),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );

        var trailers = new List<byte>();
        AddLiteral(trailers, "x-test", "ok");
        var trailerFrame = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            1,
            trailers.ToArray()
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(trailerFrame),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );

        await AwaitStreamPumpsAsync(connection);
        await Assert.That(connection.HasResponseHeaders(1)).IsTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition not met within the timeout.");
    }

    private static ComplianceTestContext NewConnection()
    {
        var connection = new ComplianceTestContext();
        connection.UserState = new ConnectionRuntimeState
        {
            Protocol = ConnectionProtocol.Http2,
            ReceivedPostPrefaceFrame = true, // preface + SETTINGS already exchanged
        };
        return connection;
    }

    private static async Task SendCompleteGetAsync(ComplianceTestContext connection, int streamId)
    {
        var headers = BuildHeadersFrame(
            Http2FrameType.Headers,
            Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream,
            streamId,
            BuildMinimalHpack("GET", "/foo")
        );
        await Http2ConnectionProcessor.ProcessAsync(
            connection,
            new ReadOnlySequence<byte>(headers),
            sendInitialSettings: false,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "ok"u8.ToArray() }
                ),
            null,
            CancellationToken.None
        );
        await AwaitStreamPumpsAsync(connection);
    }

    private static void AddLiteral(List<byte> block, string name, string value)
    {
        // Literal without indexing, new name.
        block.Add(0x00);
        block.Add((byte)name.Length);
        block.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
        block.Add((byte)value.Length);
        block.AddRange(System.Text.Encoding.ASCII.GetBytes(value));
    }

    private static int GetDataPayloadBytes(ComplianceTestContext connection)
    {
        var total = 0;
        foreach (var frameBytes in connection.SentFrames)
        {
            if (!TryReadFrame(frameBytes, out var frame) || frame is null)
                continue;
            if (frame.Type == Http2FrameType.Data)
                total += frame.Payload.Length;
        }

        return total;
    }

    private static byte[] BuildHeadersFrame(
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId,
        byte[] payload
    ) => Http2FrameCodec.EncodeFrame(type, flags, streamId, payload);

    private static Http2Frame BuildFrame(
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId,
        byte[] payload
    ) =>
        new()
        {
            Type = type,
            Flags = flags,
            StreamId = streamId,
            Length = payload.Length,
            Payload = payload,
        };

    private static byte[] BuildWindowUpdatePayload(int increment) =>
        [(byte)(increment >> 24), (byte)(increment >> 16), (byte)(increment >> 8), (byte)increment];

    private static byte[] BuildMinimalHpack(string method, string path)
    {
        // Simple non-Huffman HPACK: indexed :method if GET, literals otherwise.
        var block = new List<byte>();
        if (method == "GET")
        {
            block.Add(0x82);
        }
        else
        {
            block.Add(0x00);
            block.Add(0x07);
            block.AddRange(System.Text.Encoding.ASCII.GetBytes(":method"));
            block.Add((byte)method.Length);
            block.AddRange(System.Text.Encoding.ASCII.GetBytes(method));
        }

        // Literal without indexing, name from static table index 4 (":path").
        block.Add(0x04);
        block.Add((byte)path.Length);
        block.AddRange(System.Text.Encoding.ASCII.GetBytes(path));
        block.Add(0x86); // :scheme http (static index 6)
        return block.ToArray();
    }

    private static bool TryReadFrame(byte[] bytes, out Http2Frame? frame)
    {
        frame = null;
        if (bytes.Length < 9)
            return false;
        var length = (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
        if (bytes.Length < 9 + length)
            return false;
        var payload = new byte[length];
        Array.Copy(bytes, 9, payload, 0, length);
        frame = new Http2Frame
        {
            Length = length,
            Type = (Http2FrameType)bytes[3],
            Flags = (Http2FrameFlags)bytes[4],
            StreamId = ((bytes[5] & 0x7F) << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8],
            Payload = payload,
        };
        return true;
    }

    private static async Task AwaitStreamPumpsAsync(ComplianceTestContext connection)
    {
        var streams = (connection.UserState as ConnectionRuntimeState)?.Http2Streams;
        if (streams is null)
            return;

        var pumps = new List<Task>();
        foreach (var kvp in streams)
        {
            if (kvp.Value is { } state && state.ResponsePumpTask is { } pump)
            {
                pumps.Add(pump);
            }
        }

        foreach (var pump in pumps)
        {
            await pump;
        }
    }

    private sealed class ComplianceTestContext : ITcpConnectionContext
    {
        private readonly object _sendGate = new();
        private readonly List<byte[]> _sentFrames = new();

        public long ConnectionId => 1;
        public EndPoint RemoteEndPoint =>
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 12345);
        public DateTimeOffset ConnectedAtUtc => DateTimeOffset.MinValue;
        public DateTimeOffset LastActivityUtc => DateTimeOffset.MinValue;
        public object? UserState { get; set; }
        public string? NegotiatedProtocol => null;

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

        private Http2ErrorCode? _lastGoAwayCode;
        private Http2ErrorCode? _lastRstStreamCode;

        public Http2ErrorCode? LastGoAwayErrorCode
        {
            get
            {
                foreach (var frameBytes in SentFrames)
                {
                    if (
                        TryReadFrame(frameBytes, out var frame)
                        && frame is not null
                        && frame.Type == Http2FrameType.GoAway
                        && frame.Payload.Length >= 8
                    )
                    {
                        // GOAWAY payload: last-stream-id (4 bytes) + error code (4 bytes).
                        var off = frame.Payload.Length - 4;
                        _lastGoAwayCode = (Http2ErrorCode)(
                            (frame.Payload.Span[off] << 24)
                            | (frame.Payload.Span[off + 1] << 16)
                            | (frame.Payload.Span[off + 2] << 8)
                            | frame.Payload.Span[off + 3]
                        );
                    }
                }

                return _lastGoAwayCode;
            }
        }

        public Http2ErrorCode? LastRstStreamCode
        {
            get
            {
                foreach (var frameBytes in SentFrames)
                {
                    if (
                        TryReadFrame(frameBytes, out var frame)
                        && frame is not null
                        && frame.Type == Http2FrameType.RstStream
                        && frame.Payload.Length >= 4
                    )
                    {
                        _lastRstStreamCode = (Http2ErrorCode)(
                            (frame.Payload.Span[0] << 24)
                            | (frame.Payload.Span[1] << 16)
                            | (frame.Payload.Span[2] << 8)
                            | frame.Payload.Span[3]
                        );
                    }
                }

                return _lastRstStreamCode;
            }
        }

        public bool HasResponseHeaders(int streamId)
        {
            foreach (var frameBytes in SentFrames)
            {
                if (
                    TryReadFrame(frameBytes, out var frame)
                    && frame is not null
                    && frame.Type == Http2FrameType.Headers
                    && frame.StreamId == streamId
                    && frame.Payload.Span.Length > 0
                    && frame.Payload.Span[0] == 0x88
                )
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasEndStream(int streamId)
        {
            foreach (var frameBytes in SentFrames)
            {
                if (
                    TryReadFrame(frameBytes, out var frame)
                    && frame is not null
                    && frame.StreamId == streamId
                    && frame.Type == Http2FrameType.Data
                    && frame.HasFlag(Http2FrameFlags.EndStream)
                )
                {
                    return true;
                }
            }

            return false;
        }
    }
}
