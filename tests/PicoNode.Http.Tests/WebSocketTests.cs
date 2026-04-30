namespace PicoNode.Http.Tests;

public sealed class WebSocketTests
{
    private static HttpRequest CreateUpgradeRequest(
        string key = "dGhlIHNhbXBsZSBub25jZQ==",
        string version = "13"
    )
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Host", "localhost"),
            new("Upgrade", "websocket"),
            new("Connection", "Upgrade"),
            new("Sec-WebSocket-Key", key),
            new("Sec-WebSocket-Version", version),
        };

        return new HttpRequest
        {
            Method = "GET",
            Target = "/ws",
            Version = HttpVersion.Http11,
            HeaderFields = headers,
            Headers = headers.ToDictionary(
                h => h.Key,
                h => h.Value,
                StringComparer.OrdinalIgnoreCase
            ),
            Body = ReadOnlyMemory<byte>.Empty,
        };
    }

    [Test]
    public async Task TryUpgrade_returns_101_for_valid_upgrade_request()
    {
        var request = CreateUpgradeRequest();

        var response = WebSocketUpgrade.TryUpgrade(request);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(101);
        await Assert.That(response.ReasonPhrase).IsEqualTo("Switching Protocols");
    }

    [Test]
    public async Task TryUpgrade_includes_accept_key()
    {
        var request = CreateUpgradeRequest(key: "dGhlIHNhbXBsZSBub25jZQ==");

        var response = WebSocketUpgrade.TryUpgrade(request);

        var acceptHeader = response!.Headers.FirstOrDefault(h => h.Key == "Sec-WebSocket-Accept");
        // RFC 6455 example: dGhlIHNhbXBsZSBub25jZQ== → s3pPLMBiTxaQ9kYGzzhZRbK+xOo=
        await Assert.That(acceptHeader.Value).IsEqualTo("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }

    [Test]
    public async Task TryUpgrade_returns_null_for_non_get_request()
    {
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/ws",
            Version = HttpVersion.Http11,
            HeaderFields =
            [
                new("Upgrade", "websocket"),
                new("Connection", "Upgrade"),
                new("Sec-WebSocket-Key", "abc"),
                new("Sec-WebSocket-Version", "13"),
            ],
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Upgrade"] = "websocket",
                ["Connection"] = "Upgrade",
                ["Sec-WebSocket-Key"] = "abc",
                ["Sec-WebSocket-Version"] = "13",
            },
            Body = ReadOnlyMemory<byte>.Empty,
        };

        var response = WebSocketUpgrade.TryUpgrade(request);

        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task TryUpgrade_returns_null_without_upgrade_header()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Host", "localhost"),
            new("Connection", "Upgrade"),
            new("Sec-WebSocket-Key", "abc"),
            new("Sec-WebSocket-Version", "13"),
        };

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/ws",
            Version = HttpVersion.Http11,
            HeaderFields = headers,
            Headers = headers.ToDictionary(
                h => h.Key,
                h => h.Value,
                StringComparer.OrdinalIgnoreCase
            ),
            Body = ReadOnlyMemory<byte>.Empty,
        };

        var response = WebSocketUpgrade.TryUpgrade(request);

        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task TryUpgrade_returns_null_for_wrong_version()
    {
        var request = CreateUpgradeRequest(version: "8");

        var response = WebSocketUpgrade.TryUpgrade(request);

        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task ComputeAcceptKey_matches_rfc_example()
    {
        var key = WebSocketUpgrade.ComputeAcceptKey("dGhlIHNhbXBsZSBub25jZQ==");

        await Assert.That(key).IsEqualTo("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }

    [Test]
    public async Task EncodeFrame_and_TryReadFrame_roundtrip_text()
    {
        var payload = "Hello, WebSocket!"u8.ToArray();
        var encoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Text, payload);

        var buffer = new ReadOnlySequence<byte>(encoded);
        var success = WebSocketFrameCodec.TryReadFrame(buffer, out var frame, out var consumed);

        await Assert.That(success).IsTrue();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Fin).IsTrue();
        await Assert.That(frame.OpCode).IsEqualTo(WebSocketOpCode.Text);
        await Assert
            .That(Encoding.UTF8.GetString(frame.Payload.Span))
            .IsEqualTo("Hello, WebSocket!");
        await Assert.That(consumed).IsEqualTo(encoded.Length);
    }

    [Test]
    public async Task EncodeFrame_and_TryReadFrame_roundtrip_masked()
    {
        var payload = "Masked data"u8.ToArray();
        var encoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Binary, payload, mask: true);

        var buffer = new ReadOnlySequence<byte>(encoded);
        var success = WebSocketFrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(frame).IsNotNull();
        await Assert.That(Encoding.UTF8.GetString(frame!.Payload.Span)).IsEqualTo("Masked data");
    }

    [Test]
    public async Task TryReadFrame_returns_false_for_incomplete_header()
    {
        var buffer = new ReadOnlySequence<byte>(new byte[] { 0x81 });
        var success = WebSocketFrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsFalse();
        await Assert.That(frame).IsNull();
    }

    [Test]
    public async Task TryReadFrame_returns_false_for_incomplete_payload()
    {
        var encoded = new byte[] { 0x81, 0x05, 0x48, 0x65 }; // claims 5 bytes but only has 2
        var buffer = new ReadOnlySequence<byte>(encoded);
        var success = WebSocketFrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsFalse();
        await Assert.That(frame).IsNull();
    }

    [Test]
    public async Task EncodeFrame_close_frame()
    {
        var encoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Close, []);

        var buffer = new ReadOnlySequence<byte>(encoded);
        var success = WebSocketFrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(frame!.OpCode).IsEqualTo(WebSocketOpCode.Close);
        await Assert.That(frame.Payload.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EncodeFrame_medium_payload_uses_extended_length()
    {
        var payload = new byte[200];
        Array.Fill(payload, (byte)0x42);
        var encoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Binary, payload);

        var buffer = new ReadOnlySequence<byte>(encoded);
        var success = WebSocketFrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(frame!.Payload.Length).IsEqualTo(200);
    }

    [Test]
    public async Task Ping_pong_frame_roundtrip()
    {
        var pingPayload = "ping"u8.ToArray();
        var pingEncoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Ping, pingPayload);

        var buffer = new ReadOnlySequence<byte>(pingEncoded);
        var success = WebSocketFrameCodec.TryReadFrame(buffer, out var frame, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(frame!.OpCode).IsEqualTo(WebSocketOpCode.Ping);

        var pongEncoded = WebSocketFrameCodec.EncodeFrame(WebSocketOpCode.Pong, frame.Payload.Span);
        buffer = new ReadOnlySequence<byte>(pongEncoded);
        success = WebSocketFrameCodec.TryReadFrame(buffer, out var pong, out _);

        await Assert.That(success).IsTrue();
        await Assert.That(pong!.OpCode).IsEqualTo(WebSocketOpCode.Pong);
        await Assert.That(Encoding.UTF8.GetString(pong.Payload.Span)).IsEqualTo("ping");
    }
}
