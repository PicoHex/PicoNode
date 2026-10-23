namespace PicoJsonRpc.Tests;

/// <summary>
/// Wire contract (T7.4 / D2 = A): the bytes on the wire are JSON-RPC 2.0
/// objects, not a serializer's dump of a C# class.
/// <list type="bullet">
/// <item>every frame carries <c>"jsonrpc":"2.0"</c>;</item>
/// <item><c>params</c>/<c>result</c> are inlined as JSON <em>values</em>, not as
/// strings containing JSON (<c>"params":{"messages":[]}</c>, not
/// <c>"params":"{\"messages\":[]}"</c>);</item>
/// <item>member presence is decided by frame kind — a notification has no
/// <c>id</c>, a response carries exactly one of <c>result</c>/<c>error</c>;</item>
/// <item>an inbound numeric <c>id</c> is read as raw text and echoed back as a
/// number, so a peer that sends <c>"id":7</c> is answered with <c>"id":7</c>.</item>
/// </list>
/// <para>
/// The public API does not move (<see cref="JsonRpcFrame"/> keeps its
/// <c>string?</c> members) — only the codec's output — which is exactly why
/// these tests exist: <c>api-surface</c> cannot see a wire change.
/// </para>
/// </summary>
public sealed class WireConformanceTests
{
    /// <summary>The frame's wire line, without the NDJSON terminator.</summary>
    private static string Line(JsonRpcFrame frame) =>
        Encoding.UTF8.GetString(NdjsonFramer.Encode(frame)).TrimEnd('\n');

    [Test]
    public async Task Request_line_matches_jsonrpc_2_0()
    {
        var line = Line(JsonRpcFrame.Request("req-1", "stream", "{\"messages\":[]}"));
        await Assert
            .That(line)
            .IsEqualTo(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-1\",\"method\":\"stream\",\"params\":{\"messages\":[]}}"
            );
    }

    [Test]
    public async Task Notification_line_has_no_id_member()
    {
        var line = Line(JsonRpcFrame.Notification("chunk", "{\"text\":\"a\"}"));
        await Assert
            .That(line)
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"method\":\"chunk\",\"params\":{\"text\":\"a\"}}");
        await Assert.That(line.Contains("\"id\"", StringComparison.Ordinal)).IsFalse();

        // no params at all: the member is OMITTED (JSON-RPC 2.0 says params, if
        // present, is a structured value — `"params":null` is not conforming)
        var bare = Line(JsonRpcFrame.Notification("ping", null));
        await Assert.That(bare).IsEqualTo("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}");
    }

    [Test]
    public async Task Response_line_has_exactly_one_of_result_or_error()
    {
        var ok = Line(JsonRpcFrame.Response("req-1", "{\"ok\":true}"));
        await Assert
            .That(ok)
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"req-1\",\"result\":{\"ok\":true}}");
        await Assert.That(ok.Contains("\"error\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(ok.Contains("\"method\"", StringComparison.Ordinal)).IsFalse();

        var bad = Line(
            JsonRpcFrame.Response(
                "req-2",
                "{\"ignored\":true}",
                new JsonRpcError { Code = -32601, Message = "no such method" }
            )
        );
        await Assert
            .That(bad)
            .IsEqualTo(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-2\",\"error\":{\"code\":-32601,\"message\":\"no such method\"}}"
            );
        await Assert.That(bad.Contains("\"result\"", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Inbound_numeric_id_is_routed()
    {
        using var ms = new MemoryStream("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":\"ok\"}\n"u8.ToArray());
        var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
        await Assert.That(frames).Count().IsEqualTo(1);
        // captured as RAW TEXT — the string member carries the number's text
        await Assert.That(frames[0].Id).IsEqualTo("7");
        await Assert.That(frames[0].Result).IsEqualTo("\"ok\"");

        // ...and echoing it back keeps the number a number: a peer that sent
        // "id":7 must not be answered with "id":"7"
        var echo = Line(JsonRpcFrame.Response(frames[0].Id!, frames[0].Result));
        await Assert.That(echo).IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":\"ok\"}");
    }

    [Test]
    public async Task Inbound_batch_line_does_not_kill_the_channel()
    {
        // batching is unsupported: the line is NOT decoded as a frame and NOT a
        // framing failure — it becomes a -32600 response the caller can send
        // back, and the stream keeps flowing (a FormatException here would run
        // into HandleEof and kill the channel for every later call).
        using var ms = new MemoryStream(
            "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"a\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"b\"}]\n{\"jsonrpc\":\"2.0\",\"id\":\"req-3\",\"result\":{\"ok\":true}}\n"u8.ToArray()
        );
        var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
        await Assert.That(frames).Count().IsEqualTo(2);

        await Assert.That(frames[0].Error).IsNotNull();
        await Assert.That(frames[0].Error!.Code).IsEqualTo(-32600);
        await Assert.That(frames[0].Id).IsNull();
        await Assert.That(frames[0].Method).IsNull();
        await Assert.That(frames[0].Result).IsNull();

        // the channel is still alive: the following frame is decoded normally
        await Assert.That(frames[1].Id).IsEqualTo("req-3");
        await Assert.That(frames[1].Result).IsEqualTo("{\"ok\":true}");
    }

    /// <summary>
    /// Reader leniency (the other half of D2 = A): the shape change must be a
    /// ONE-WAY door for a peer still on the pre-T7.4 writer. Such a writer emits
    /// EVERY member on every frame — so its success response carries
    /// <c>"method":null</c> and its error response carries BOTH <c>"result":null</c>
    /// and a populated <c>error</c>. A reader that treated "member present" as
    /// "member set" would throw on the first response an old peer sent, turning
    /// a wire upgrade into a channel kill. Every line below is byte-for-byte
    /// what <c>bffafc1</c> actually emitted (captured from the base build).
    /// </summary>
    [Test]
    public async Task Old_format_peer_frames_are_read_without_throwing()
    {
        // old success response — "method":null and "error":null are PRESENT but null
        using var okMs = new MemoryStream(
            "{\"id\":\"req-1\",\"method\":null,\"result\":\"{\\\"ok\\\":true}\",\"params\":null,\"error\":null}\n"u8.ToArray()
        );
        var ok = await NdjsonFramer.ReadAsync(okMs, default).ToListAsync();
        await Assert.That(ok).Count().IsEqualTo(1);
        // NOT a request (id + null method) — it routes as a response, which is
        // what an old peer's answer has to look like to the client
        await Assert.That(ok[0].Method).IsNull();
        await Assert.That(ok[0].Id).IsEqualTo("req-1");
        await Assert.That(ok[0].Error).IsNull();

        // old error response — "result":null sits NEXT TO a real error; the error
        // must win, and the reader must not choke on the null sibling
        using var errMs = new MemoryStream(
            "{\"id\":\"req-2\",\"method\":null,\"result\":null,\"params\":null,\"error\":{\"Code\":-32601,\"Message\":\"no such method\",\"Data\":null}}\n"u8.ToArray()
        );
        var err = await NdjsonFramer.ReadAsync(errMs, default).ToListAsync();
        await Assert.That(err).Count().IsEqualTo(1);
        await Assert.That(err[0].Method).IsNull();
        await Assert.That(err[0].Error).IsNotNull();
        await Assert.That(err[0].Error!.Code).IsEqualTo(-32601);
        await Assert.That(err[0].Error!.Message).IsEqualTo("no such method");
        await Assert.That(err[0].Error!.Data).IsNull();

        // old notification — "id":null is PRESENT, so this is still a
        // notification (id + method both null is not the both-set request case)
        using var notifMs = new MemoryStream(
            "{\"id\":null,\"method\":\"chunk\",\"result\":null,\"params\":\"{\\\"text\\\":\\\"a\\\"}\",\"error\":null}\n"u8.ToArray()
        );
        var notif = await NdjsonFramer.ReadAsync(notifMs, default).ToListAsync();
        await Assert.That(notif).Count().IsEqualTo(1);
        await Assert.That(notif[0].Method).IsEqualTo("chunk");
        await Assert.That(notif[0].Id).IsNull();
        await Assert.That(notif[0].Error).IsNull();
    }

    [Test]
    public async Task Empty_string_id_stays_a_json_string()
    {
        // `Id` is a plain string member, so "" is reachable; it must serialize as
        // "" and never reach the numeric-echo path (which reads the first char)
        var line = Line(JsonRpcFrame.Response("", "{\"ok\":true}"));
        await Assert
            .That(line)
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"\",\"result\":{\"ok\":true}}");
    }

    [Test]
    public async Task Inline_payload_roundtrips_through_the_framer()
    {
        var resp = JsonRpcFrame.Response("req-1", "{\"ok\":true}");
        using var ms = new MemoryStream(NdjsonFramer.Encode(resp));
        var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].Id).IsEqualTo("req-1");
        // the payload member is raw JSON text on BOTH sides of the wire
        await Assert.That(frames[0].Result).IsEqualTo("{\"ok\":true}");
        await Assert.That(frames[0].Error).IsNull();
    }

    [Test]
    public async Task Non_json_payload_falls_back_to_a_string()
    {
        // a caller that hands the codec a non-JSON string must not get a broken
        // line: it degrades to a JSON string instead of throwing
        var line = Line(JsonRpcFrame.Notification("chunk", "not json"));
        await Assert.That(line).IsEqualTo("{\"jsonrpc\":\"2.0\",\"method\":\"chunk\",\"params\":\"not json\"}");
    }

    /// <summary>
    /// The golden wire snapshot (T7.4 creates it BEFORE the encoder changes, so
    /// the shape change is pinned by a test and not only by a commit message).
    /// Eight frame kinds, byte for byte, <c>\n</c>-joined — the same corpus T7.7
    /// pins to a fixture file, here inline so the shape cannot drift silently.
    /// </summary>
    [Test]
    public async Task Wire_snapshot_matches_the_golden_lines()
    {
        const string expected = """
            {"jsonrpc":"2.0","id":"req-1","method":"stream","params":{"messages":[]}}
            {"jsonrpc":"2.0","method":"chunk","params":{"text":"a"}}
            {"jsonrpc":"2.0","id":"req-1","result":{"ok":true}}
            {"jsonrpc":"2.0","id":"req-2","error":{"code":-32601,"message":"no such method","data":{"method":"nope"}}}
            {"jsonrpc":"2.0","method":"stream","params":{"_streamId":"s-1","text":"b"}}
            {"jsonrpc":"2.0","method":"cancel","params":{"id":"req-1"}}
            {"jsonrpc":"2.0","id":7,"result":"ok"}
            {"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"batch unsupported"}}
            """;

        JsonRpcFrame[] corpus =
        [
            JsonRpcFrame.Request("req-1", "stream", "{\"messages\":[]}"),
            JsonRpcFrame.Notification("chunk", "{\"text\":\"a\"}"),
            JsonRpcFrame.Response("req-1", "{\"ok\":true}"),
            JsonRpcFrame.Response(
                "req-2",
                error: new JsonRpcError
                {
                    Code = -32601,
                    Message = "no such method",
                    Data = "{\"method\":\"nope\"}",
                }
            ),
            JsonRpcFrame.Notification("stream", "{\"_streamId\":\"s-1\",\"text\":\"b\"}"),
            JsonRpcFrame.Notification("cancel", "{\"id\":\"req-1\"}"),
            JsonRpcFrame.Response("7", "\"ok\""), // numeric id, echoed as a number
            new JsonRpcFrame
            {
                Error = new JsonRpcError { Code = -32600, Message = "batch unsupported" },
            },
        ];

        var actual = string.Join(
            "\n",
            corpus.Select(f => Encoding.UTF8.GetString(NdjsonFramer.Encode(f)).TrimEnd('\n'))
        );
        await Assert.That(actual).IsEqualTo(expected.ReplaceLineEndings("\n").TrimEnd('\n'));
    }
}
