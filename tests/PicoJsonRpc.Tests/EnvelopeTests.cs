namespace PicoJsonRpc.Tests;

public sealed class EnvelopeTests
{
    [Test]
    public async Task Envelope_RoundTrip()
    {
        var req = JsonRpcFrame.Request("1", "ping", "{\"a\":1}");
        // A0 probe: PicoJetson Serialize<T> returns string; Deserialize<T> takes ReadOnlySpan<byte>.
        var json = JsonSerializer.Serialize(req, JsonRpcCodec.Camel);
        var back = JsonSerializer.Deserialize<JsonRpcFrame>(
            Encoding.UTF8.GetBytes(json),
            JsonRpcCodec.Camel
        );
        await Assert.That(back).IsEquivalentTo(req);
    }

    /// <summary>Wire contract lock: JSON-RPC 2.0 reserved members are camelCase
    /// (id/method/params/result/error) and params/result embed raw JSON text as
    /// JSON strings (spec §9.8) — the fixtures and thin packages depend on this.</summary>
    [Test]
    public async Task Wire_Shape_IsCamelCase_WithStringPayloads()
    {
        var req = JsonRpcFrame.Request("1", "stream", "{\"messages\":[]}");
        var json = JsonRpcCodec.Serialize(req);
        await Assert
            .That(json)
            .IsEqualTo(
                "{\"id\":\"1\",\"method\":\"stream\",\"result\":null,\"params\":\"{\\\"messages\\\":[]}\",\"error\":null}"
            );

        var notif = JsonRpcFrame.Notification("chunk", "{\"text\":\"a\"}");
        await Assert
            .That(JsonRpcCodec.Serialize(notif))
            .IsEqualTo(
                "{\"id\":null,\"method\":\"chunk\",\"result\":null,\"params\":\"{\\\"text\\\":\\\"a\\\"}\",\"error\":null}"
            );
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
