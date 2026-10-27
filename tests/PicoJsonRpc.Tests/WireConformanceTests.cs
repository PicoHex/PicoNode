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

    /// <summary>F1 at the STRING face (<see cref="JsonRpcCodec.Serialize"/>): a
    /// frame's text carries no line break at all, terminator included — so even a
    /// caller that appends its own <c>\n</c> cannot produce two lines.</summary>
    [Test]
    public async Task Serialize_never_contains_a_line_break()
    {
        var json = JsonRpcCodec.Serialize(
            JsonRpcFrame.Request("req-1", "stream", "{\n \"messages\": []\n}")
        );
        await Assert.That(Count(json, '\n')).IsEqualTo(0);
        await Assert.That(Count(json, '\r')).IsEqualTo(0);
        await Assert
            .That(json)
            .IsEqualTo(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-1\",\"method\":\"stream\",\"params\":{\"messages\":[]}}"
            );
    }

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

    /// <summary>
    /// F1 (review fix round 1): NDJSON has ONE line per frame, so the encoder
    /// must be structurally incapable of emitting a line break — a peer-triggered
    /// payload or id carrying CR/LF used to split one frame into several lines.
    /// A pretty-printed payload is <em>normalised</em> (re-emitted compactly),
    /// not rejected: the caller's raw text is still the same JSON value.
    /// </summary>
    [Test]
    public async Task Pretty_printed_payload_is_normalised_to_one_line()
    {
        // a NOTE on the round trip below: our reader applies the downlink union
        // rule (a line with BOTH id and method is a child REQUEST — a protocol
        // error), so the frame read back has to be a notification/response. The
        // normalisation under test is the same in either case.
        var frame = JsonRpcFrame.Notification("stream", "{\n  \"messages\": []\n}");
        var line = Line(frame);
        await Assert.That(Count(line, '\n')).IsEqualTo(0);
        await Assert.That(Count(line, '\r')).IsEqualTo(0);
        // same value, compact: whitespace between tokens is not information
        await Assert
            .That(line)
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"method\":\"stream\",\"params\":{\"messages\":[]}}");
        await AssertRoundTripIsSingleLine(frame, expectedParams: "{\"messages\":[]}");

        // ...and the same text on a REQUEST produces the same single line
        await Assert
            .That(Line(JsonRpcFrame.Request("req-1", "stream", "{\n  \"messages\": []\n}")))
            .IsEqualTo(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-1\",\"method\":\"stream\",\"params\":{\"messages\":[]}}"
            );
    }

    /// <summary>F1: a payload whose text ends in a newline (or CRLF) — the
    /// reviewer's reproduction class — must not append a stray line.</summary>
    [Test]
    public async Task Payload_with_trailing_whitespace_is_one_line()
    {
        foreach (var trailing in new[] { "\n", "\r\n", "  \n  ", "\r" })
        {
            var frame = JsonRpcFrame.Notification("chunk", "{\"text\":\"a\"}" + trailing);
            var line = Line(frame);
            await Assert.That(Count(line, '\n')).IsEqualTo(0);
            await Assert.That(Count(line, '\r')).IsEqualTo(0);
            await Assert
                .That(line)
                .IsEqualTo("{\"jsonrpc\":\"2.0\",\"method\":\"chunk\",\"params\":{\"text\":\"a\"}}");
            await AssertRoundTripIsSingleLine(frame, expectedParams: "{\"text\":\"a\"}");
        }
    }

    /// <summary>F1: an <c>id</c> whose raw text carries whitespace used to be
    /// re-emitted <c>verbatim</c>, producing <c>"id":7⏎</c> — a frame that OUR
    /// OWN reader rejects (<c>'7' is an invalid end of a number</c>). Re-emitting
    /// the parsed number keeps the peer's numeric id AND the line intact.</summary>
    [Test]
    public async Task Id_text_with_whitespace_is_one_line_and_round_trips()
    {
        foreach (var id in new[] { "7\n", " 7 ", "7\r\n", "\n7" })
        {
            var line = Line(JsonRpcFrame.Response(id, "\"ok\""));
            await Assert.That(Count(line, '\n')).IsEqualTo(0);
            await Assert.That(Count(line, '\r')).IsEqualTo(0);
            await Assert.That(line).IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":\"ok\"}");

            // ...and the round trip through our own reader succeeds
            using var ms = new MemoryStream(NdjsonFramer.Encode(JsonRpcFrame.Response(id, "\"ok\"")));
            var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(frames).Count().IsEqualTo(1);
            await Assert.That(frames[0].Id).IsEqualTo("7");
        }
    }

    /// <summary>F1, stated as the property rather than as cases: no frame kind may
    /// put a line break in its emitted bytes — the ONLY <c>\n</c> is the NDJSON
    /// terminator <see cref="NdjsonFramer.Encode"/> appends.</summary>
    [Test]
    public async Task No_emitted_frame_contains_a_line_break()
    {
        JsonRpcFrame[] frames =
        [
            JsonRpcFrame.Request("req-1", "m", "{\n \"a\": 1\n}"),
            JsonRpcFrame.Request(" 7 ", "m", "\r\n{\"a\":1}\r\n"),
            JsonRpcFrame.Notification("chunk", "[\n1,\n2\n]"),
            JsonRpcFrame.Notification("chunk", "not json\nwith a break"),
            JsonRpcFrame.Notification("chunk", "\r\n\t "),
            JsonRpcFrame.Response("7\n", "{\n\"ok\": true\n}"),
            JsonRpcFrame.Response("req-2", error: new JsonRpcError
            {
                Code = -32601,
                Message = "no\nsuch method",
                Data = "{\n\"method\": \"nope\"\n}",
            }),
        ];

        foreach (var frame in frames)
        {
            var bytes = NdjsonFramer.Encode(frame);
            var terminator = bytes[^1];
            await Assert.That(terminator).IsEqualTo((byte)'\n');
            // the ONLY line break in the frame is the terminator itself
            await Assert.That(Array.IndexOf(bytes, (byte)'\n')).IsEqualTo(bytes.Length - 1);
            await Assert.That(Array.IndexOf(bytes, (byte)'\r')).IsEqualTo(-1);
        }

        // ...and every frame the DOWNLINK can carry (no id+method union
        // violation) is still exactly one frame when read back
        foreach (var frame in frames.Where(f => f.Id is null || f.Method is null))
        {
            using var ms = new MemoryStream(NdjsonFramer.Encode(frame));
            var read = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(read).Count().IsEqualTo(1);
        }
    }

    /// <summary>
    /// F2 (review fix round 1): the encoder's depth gate and the reader's parse
    /// options were two different numbers (256 vs STJ's default 64), so a payload
    /// nested 65–256 deep was <em>emitted inline but unreadable by us</em>. The two
    /// are now one limit, and this pins the boundary from both sides: at the limit
    /// the payload stays inline and round-trips; past it, it degrades to a JSON
    /// string (still one readable line) instead of becoming a poisoned frame.
    /// </summary>
    [Test]
    public async Task Payload_depth_boundary_is_emitted_and_readable()
    {
        // inline at and below the limit
        foreach (var depth in new[] { 2, 8, JsonRpcWire.MaxPayloadDepth })
        {
            var payload = NestedObject(depth);
            var bytes = NdjsonFramer.Encode(JsonRpcFrame.Notification("n", payload));
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\n');
            // inline: the payload sits in `params` as a VALUE (its own `{`), not as
            // a quoted string containing JSON
            await Assert.That(text.Contains("\"params\":{", StringComparison.Ordinal)).IsTrue();
            await Assert.That(text).Contains(payload);

            using var ms = new MemoryStream(bytes);
            var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(frames).Count().IsEqualTo(1);
            await Assert.That(frames[0].Params).IsEqualTo(payload);
        }

        // ONE past the limit: still a single, readable line (degraded to a string,
        // the same fallback a non-JSON payload takes) — never an inline payload we
        // could not parse back
        var tooDeep = NestedObject(JsonRpcWire.MaxPayloadDepth + 1);
        var deepBytes = NdjsonFramer.Encode(JsonRpcFrame.Notification("n", tooDeep));
        var deepText = Encoding.UTF8.GetString(deepBytes).TrimEnd('\n');
        await Assert.That(Count(deepText, '\n')).IsEqualTo(0);
        await Assert.That(deepText.Contains("\"a\":{", StringComparison.Ordinal)).IsFalse();
        using var deepMs = new MemoryStream(deepBytes);
        var deepFrames = await NdjsonFramer.ReadAsync(deepMs, default).ToListAsync();
        await Assert.That(deepFrames).Count().IsEqualTo(1);
        await Assert.That(deepFrames[0].Params![0]).IsEqualTo('"'); // a JSON string, not a value
    }

    /// <summary>
    /// The documented LIMIT of "one line per frame" (T7.4 re-review follow-up 3). A raw
    /// U+2028 (LINE SEPARATOR), U+2029 (PARAGRAPH SEPARATOR) or U+0085 (NEL) inside an
    /// otherwise one-line payload goes out VERBATIM: none of them is CR/LF, so the
    /// normalisation gate leaves the text alone and <see cref="Utf8JsonWriter"/> does
    /// not escape a character it considers printable.
    /// <para>
    /// The assertion is deliberately two-sided so the doc cannot drift either way: the
    /// emitted bytes carry ONLY the NDJSON terminator as a CR/LF (an LF/CR reader sees
    /// one line), and the payload really does contain the raw separators (a
    /// Unicode-aware splitter would see more). If a future change escapes them, this
    /// test fails and the doc must be corrected in the same commit.
    /// </para>
    /// </summary>
    [Test]
    public async Task Unicode_line_separators_stay_verbatim_inside_a_one_line_payload()
    {
        foreach (var separator in new[] { '\u2028', '\u2029', '\u0085' })
        {
            var payload = $"{{\"text\":\"a{separator}b\"}}";
            var bytes = NdjsonFramer.Encode(JsonRpcFrame.Notification("chunk", payload));

            // LF/CR reader: exactly one line, and the ONLY line break is the terminator
            await Assert.That(Array.IndexOf(bytes, (byte)'\n')).IsEqualTo(bytes.Length - 1);
            await Assert.That(Array.IndexOf(bytes, (byte)'\r')).IsEqualTo(-1);

            // ...and the separator is still there, raw, in the emitted bytes
            var text = Encoding.UTF8.GetString(bytes);
            await Assert.That(text.Contains(separator, StringComparison.Ordinal)).IsTrue();

            // a Unicode-aware splitter would count two lines — the documented limit,
            // asserted so "one line" is never claimed without its qualification
            await Assert.That(text.Split(separator).Length).IsEqualTo(2);

            // our own reader still accepts the frame and returns the payload unchanged
            using var ms = new MemoryStream(bytes);
            var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(frames).Count().IsEqualTo(1);
            await Assert.That(frames[0].Params).IsEqualTo(payload);
        }
    }

    /// <summary>
    /// F2 at the DEEPEST payload position (T7.4 re-review follow-up 2). The T7.4
    /// report claimed the depth boundary was covered "including <c>error.data</c>",
    /// but the test only exercised <c>params</c> — mutating
    /// <c>PayloadEnvelopeDepth</c> to 1 still passed, which means the envelope
    /// levels the reader must allow for were never pinned.
    /// <para>
    /// <c>error.data</c> sits three levels below the line root (root object → error
    /// object → data value), so a payload nested <c>MaxPayloadDepth</c> deep needs
    /// <c>MaxPayloadDepth + 3</c> of reader depth. Any envelope level the reader's
    /// limit forgets, this test fails.
    /// </para>
    /// </summary>
    [Test]
    public async Task Payload_depth_boundary_holds_in_error_data_too()
    {
        // at the limit: the data payload is inlined AND read back identically
        var payload = NestedObject(JsonRpcWire.MaxPayloadDepth);
        var bytes = NdjsonFramer.Encode(ErrorWithData(payload));
        var text = Encoding.UTF8.GetString(bytes).TrimEnd('\n');
        await Assert.That(text.Contains("\"data\":{", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Count(text, '\n')).IsEqualTo(0);
        using (var ms = new MemoryStream(bytes))
        {
            var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(frames).Count().IsEqualTo(1);
            await Assert.That(frames[0].Error!.Data).IsEqualTo(payload);
        }

        // one past it: `data` degrades to a JSON string (the ordinary fallback) and
        // the line is still one frame our own reader accepts
        var tooDeep = NestedObject(JsonRpcWire.MaxPayloadDepth + 1);
        using (var ms = new MemoryStream(NdjsonFramer.Encode(ErrorWithData(tooDeep))))
        {
            var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(frames).Count().IsEqualTo(1);
            await Assert.That(frames[0].Error!.Data![0]).IsEqualTo('"'); // a string
        }

        // The reader's limit must COVER the deepest line this writer produces, and the
        // measurement is independent of the constants under test: the payload's own
        // root is its level 1, and `error.data` puts two envelope objects above it
        // (the frame root and the error object) — so the line nests MaxPayloadDepth+2.
        // Forgetting either envelope level in the reader breaks this.
        var nested = NestingDepth(Encoding.UTF8.GetString(bytes).TrimEnd('\n'));
        await Assert.That(nested).IsEqualTo(JsonRpcWire.MaxPayloadDepth + 2);
        await Assert.That(nested).IsLessThanOrEqualTo(JsonRpcWire.MaxFrameDepth);
    }

    /// <summary>F4 (review fix round 1): the PAYLOAD TEXT decides the emitted JSON
    /// type — <c>"7"</c> ships as the number <c>7</c>, <c>"true"</c> as a boolean,
    /// <c>"null"</c> as null, <c>"ok"</c> as the string <c>"ok"</c> — and malformed
    /// text degrades to a quoted string rather than a broken line. Documented on
    /// <see cref="JsonRpcFrame"/>; pinned here so the doc cannot drift.</summary>
    [Test]
    public async Task Payload_text_decides_the_emitted_json_type()
    {
        await Assert
            .That(Line(JsonRpcFrame.Response("id-1", "7")))
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"id-1\",\"result\":7}");
        await Assert
            .That(Line(JsonRpcFrame.Response("id-1", "true")))
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"id-1\",\"result\":true}");
        await Assert
            .That(Line(JsonRpcFrame.Response("id-1", "null")))
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"id-1\",\"result\":null}");
        await Assert
            .That(Line(JsonRpcFrame.Response("id-1", "ok")))
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"id-1\",\"result\":\"ok\"}");

        // malformed text does NOT break the line: it degrades to a JSON string
        await Assert
            .That(Line(JsonRpcFrame.Response("id-1", "{\"ok\":true")))
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"id-1\",\"result\":\"{\\u0022ok\\u0022:true\"}");
    }

    /// <summary>
    /// T7.4 re-review follow-up 1. An unpaired surrogate in CALLER text cannot be
    /// transcoded to UTF-8, and after T7.4 the hand-written writer let that
    /// <see cref="ArgumentException"/> escape <see cref="JsonRpcCodec.Serialize"/> —
    /// where the pre-T7.4 serializer had quietly substituted U+FFFD. A caller's
    /// malformed STRING must not become an exception out of the codec (the same rule
    /// that makes malformed JSON text degrade to a quoted string): it is substituted,
    /// and the frame stays one readable line.
    /// <para>
    /// Substitution happens BEFORE the "is it JSON" gate, so payload text that is
    /// valid JSON apart from the lone surrogate keeps its shape (<c>params</c> stays
    /// an inline object) instead of degrading to a quoted string.
    /// </para>
    /// </summary>
    [Test]
    public async Task Unpaired_surrogate_in_caller_text_is_substituted_not_thrown()
    {
        // valid JSON apart from one lone surrogate: the payload must stay STRUCTURED
        var inParams = JsonRpcFrame.Notification("chunk", "{\"text\":\"\uD800\"}");
        var paramsLine = Line(inParams);
        await Assert.That(Count(paramsLine, '\n')).IsEqualTo(0);
        await Assert.That(Count(paramsLine, '\r')).IsEqualTo(0);
        await Assert.That(paramsLine.Contains("\"params\":{", StringComparison.Ordinal)).IsTrue();
        await AssertRoundTripIsSingleLine(inParams, "{\"text\":\"\uFFFD\"}");

        // the same payload in the RESULT position — an independent writer call, read
        // back from `result` rather than `params`
        using (var ms = new MemoryStream(
            NdjsonFramer.Encode(JsonRpcFrame.Response("id-1", "{\"text\":\"\uDC00\"}"))
        ))
        {
            var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(frames).Count().IsEqualTo(1);
            await Assert.That(frames[0].Result).IsEqualTo("{\"text\":\"\uFFFD\"}");
        }

        // ...and in `error.data`, the deepest payload position
        var withData = JsonRpcFrame.Response(
            "id-2",
            error: new JsonRpcError
            {
                Code = -1,
                Message = "m",
                Data = "{\"deep\":\"\uD800\"}",
            }
        );
        using (var ms = new MemoryStream(NdjsonFramer.Encode(withData)))
        {
            var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(frames).Count().IsEqualTo(1);
            await Assert.That(frames[0].Error!.Data).IsEqualTo("{\"deep\":\"\uFFFD\"}");
        }

        // text that is not JSON at all takes the quoted-string fallback — also
        // substituted, also one line
        var plain = JsonRpcFrame.Notification("chunk", "abc\uD800def");
        await Assert.That(Count(Line(plain), '\n')).IsEqualTo(0);
        using (var ms = new MemoryStream(NdjsonFramer.Encode(plain)))
        {
            var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
            await Assert.That(frames).Count().IsEqualTo(1);
            var text = System.Text.Json.JsonDocument
                .Parse(frames[0].Params!)
                .RootElement.GetString();
            await Assert.That(text).IsEqualTo("abc\uFFFDdef");
        }

        // the id goes through WriteStringValue, which substitutes on its own — pinned
        // here so all three writer entry points share one documented rule. The
        // substitution arrives ESCAPED (WriteStringValue's default encoder escapes
        // non-ASCII), unlike the raw inline payload path above: hence the verbatim
        // escape below.
        const string escaped = @"\uFFFD";
        await Assert
            .That(Line(JsonRpcFrame.Response("\uDC00", "1")))
            .IsEqualTo($"{{\"jsonrpc\":\"2.0\",\"id\":\"{escaped}\",\"result\":1}}");
    }

    /// <summary>F1's other half (review fix round 1): normalisation is HAZARD-SCOPED.
    /// A payload that is already one line goes out byte-for-byte — its non-ASCII
    /// text included — so the fix cannot be a silent re-encoder of every payload
    /// (round-tripping through <c>JsonElement.WriteTo</c> would turn a CJK payload
    /// into escapes, changing bytes a peer already validates).</summary>
    [Test]
    public async Task One_line_payload_keeps_its_exact_bytes()
    {
        const string payload = "{\"text\":\"你好 é😀\",\"n\":1.0}";
        var line = Line(JsonRpcFrame.Notification("chunk", payload));
        await Assert
            .That(line)
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"method\":\"chunk\",\"params\":" + payload + "}");

        await AssertRoundTripIsSingleLine(JsonRpcFrame.Notification("chunk", payload), payload);
    }

    private static int Count(string text, char c)
    {
        var n = 0;
        foreach (var ch in text)
            if (ch == c)
                n++;
        return n;
    }

    /// <summary>`{"a":{"a":…1…}}</summary>` nested <paramref name="depth"/> deep.</param>
    private static string NestedObject(int depth) =>
        string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);

    /// <summary>An error response carrying <paramref name="data"/> as `error.data` —
    /// the deepest payload position on the wire.</summary>
    private static JsonRpcFrame ErrorWithData(string data) =>
        JsonRpcFrame.Response(
            "id-1",
            error: new JsonRpcError
            {
                Code = -1,
                Message = "m",
                Data = data,
            }
        );

    /// <summary>The JSON nesting depth of a line, counted independently of the
    /// encoder and the reader (every opening brace/bracket opens one level), so a
    /// wrong envelope constant cannot be self-certified by the code under test.</summary>
    private static int NestingDepth(string line)
    {
        var depth = 0;
        var deepest = 0;
        var inString = false;
        var escaped = false;
        foreach (var c in line)
        {
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    depth++;
                    deepest = Math.Max(deepest, depth);
                    break;
                case '}' or ']':
                    depth--;
                    break;
            }
        }
        return deepest;
    }


    private static async Task AssertRoundTripIsSingleLine(JsonRpcFrame frame, string expectedParams)
    {
        using var ms = new MemoryStream(NdjsonFramer.Encode(frame));
        var frames = await NdjsonFramer.ReadAsync(ms, default).ToListAsync();
        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].Params).IsEqualTo(expectedParams);
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
