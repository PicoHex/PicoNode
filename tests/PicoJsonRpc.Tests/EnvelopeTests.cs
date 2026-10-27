namespace PicoJsonRpc.Tests;

public sealed class EnvelopeTests
{
    [Test]
    public async Task Envelope_RoundTrip()
    {
        var req = JsonRpcFrame.Request("req-1", "ping", "{\"a\":1}");
        // the STRING face of the codec (what a frame looks like as text) must
        // round-trip: inline payload → raw payload text
        var json = JsonRpcCodec.Serialize(req);
        var back = JsonRpcCodec.Deserialize<JsonRpcFrame>(json);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Id).IsEqualTo(req.Id);
        await Assert.That(back.Method).IsEqualTo(req.Method);
        await Assert.That(back.Params).IsEqualTo(req.Params);
        await Assert.That(back.Result).IsNull();
        await Assert.That(back.Error).IsNull();
    }

    /// <summary>Wire contract lock (T7.4 / D2 = A): a frame is a JSON-RPC 2.0
    /// object — <c>jsonrpc</c> always present, <c>params</c>/<c>result</c> inlined as
    /// JSON <em>values</em> (not strings containing JSON), and only the members the
    /// frame kind uses (a notification has no <c>id</c>). The framer-level byte
    /// snapshot lives in <see cref="WireConformanceTests"/>; this is the string
    /// face of the same contract. The id below is NOT numeric-looking on purpose:
    /// a numeric id's raw text is echoed back as a JSON number (see
    /// <see cref="WireConformanceTests.Inbound_numeric_id_is_routed"/>), a string
    /// id stays a JSON string.</summary>
    [Test]
    public async Task Wire_Shape_Is_JsonRpc_2_0_WithInlinedPayloads()
    {
        var req = JsonRpcFrame.Request("req-1", "stream", "{\"messages\":[]}");
        var json = JsonRpcCodec.Serialize(req);
        await Assert
            .That(json)
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"req-1\",\"method\":\"stream\",\"params\":{\"messages\":[]}}");

        var notif = JsonRpcFrame.Notification("chunk", "{\"text\":\"a\"}");
        await Assert
            .That(JsonRpcCodec.Serialize(notif))
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"method\":\"chunk\",\"params\":{\"text\":\"a\"}}");
    }

    /// <summary>
    /// F4 (review fix round 1): <c>Response(id, resultJson, error)</c> with BOTH
    /// payloads cannot be answered — JSON-RPC 2.0 allows exactly one of
    /// <c>result</c>/<c>error</c>. The factory DISCARDED the result silently; the
    /// discard is now stated on <see cref="JsonRpcFrame"/> and pinned here, so the
    /// behaviour is a documented decision instead of a surprise. The error wins.
    /// </summary>
    [Test]
    public async Task Response_factory_drops_the_result_when_an_error_is_set()
    {
        var both = JsonRpcFrame.Response(
            "id-1",
            "{\"ignored\":true}",
            new JsonRpcError { Code = -1, Message = "boom" }
        );
        await Assert.That(both.Result).IsNull();
        await Assert.That(both.Error).IsNotNull();
        await Assert.That(both.Error!.Code).IsEqualTo(-1);
        await Assert
            .That(JsonRpcCodec.Serialize(both))
            .IsEqualTo("{\"jsonrpc\":\"2.0\",\"id\":\"id-1\",\"error\":{\"code\":-1,\"message\":\"boom\"}}");

        // ...and a result-only response keeps its payload
        var ok = JsonRpcFrame.Response("id-1", "{\"ok\":true}");
        await Assert.That(ok.Result).IsEqualTo("{\"ok\":true}");
        await Assert.That(ok.Error).IsNull();
    }

    [Test]
    public async Task JsonRpcCodec_TypedPayloadText_RoundTrips()
    {
        var text = JsonRpcCodec.Serialize(new StreamChunkProbe { Text = "Hel" });
        await Assert.That(text).IsEqualTo("{\"text\":\"Hel\"}");
        var back = JsonRpcCodec.Deserialize<StreamChunkProbe>(text);
        await Assert.That(back!.Text).IsEqualTo("Hel");
    }

    [Test]
    public async Task Notification_And_Response_Shapes()
    {
        var notif = JsonRpcFrame.Notification("chunk", "{\"text\":\"a\"}");
        await Assert.That(notif.Method).IsEqualTo("chunk");
        await Assert.That(notif.Id).IsNull();
        await Assert.That(notif.Params).IsEqualTo("{\"text\":\"a\"}");

        var resp = JsonRpcFrame.Response("9", "null");
        await Assert.That(resp.Id).IsEqualTo("9");
        await Assert.That(resp.Result).IsEqualTo("null");
        await Assert.That(resp.Method).IsNull();

        var err = JsonRpcFrame.Response(
            "9",
            error: new JsonRpcError { Code = -1, Message = "boom" }
        );
        await Assert.That(err.Error!.Code).IsEqualTo(-1);
        await Assert.That(err.Error!.Message).IsEqualTo("boom");
    }
}

[PicoSerializable]
internal sealed class StreamChunkProbe
{
    public string Text { get; set; } = string.Empty;
}
