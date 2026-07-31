namespace PicoNode.Http.Tests;

public sealed class HpackMinimalEncodingTests
{
    [Test]
    public async Task EncodeMinimalHpack_decodes_GET_root()
    {
        // Arrange
        var encoded = Http1ConnectionProcessor.EncodeMinimalHpack("GET", "/");

        // Act: verify HPACK decodes successfully
        var success = HpackDecoder.TryDecode(encoded, out var headers);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(headers).Count().IsEqualTo(3);
        await Assert.That(headers[0]).IsEqualTo((":method", "GET"));
        await Assert.That(headers[1]).IsEqualTo((":path", "/"));
        await Assert.That(headers[2]).IsEqualTo((":scheme", "http"));
    }

    [Test]
    public async Task EncodeMinimalHpack_decodes_POST_path()
    {
        // Arrange
        var encoded = Http1ConnectionProcessor.EncodeMinimalHpack("POST", "/users");

        // Act: verify HPACK decodes successfully
        var success = HpackDecoder.TryDecode(encoded, out var headers);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(headers).Count().IsEqualTo(3);
        await Assert.That(headers[0]).IsEqualTo((":method", "POST"));
        await Assert.That(headers[1]).IsEqualTo((":path", "/users"));
        await Assert.That(headers[2]).IsEqualTo((":scheme", "http"));
    }

    [Test]
    public async Task EncodeMinimalHpack_decodes_PUT_path()
    {
        // Arrange
        // PUT triggers the literal name encoding path (not static table index)
        var encoded = Http1ConnectionProcessor.EncodeMinimalHpack("PUT", "/api/users/123");

        // Act: verify HPACK decodes successfully
        var success = HpackDecoder.TryDecode(encoded, out var headers);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(headers).Count().IsEqualTo(3);
        await Assert.That(headers[0]).IsEqualTo((":method", "PUT"));
        await Assert.That(headers[1]).IsEqualTo((":path", "/api/users/123"));
        await Assert.That(headers[2]).IsEqualTo((":scheme", "http"));
    }

    [Test]
    public async Task EncodeMinimalHpack_decodes_long_path()
    {
        // Arrange
        // Path > 127 chars triggers the variable-length integer encoding
        var longPath = "/" + new string('x', 200);
        var encoded = Http1ConnectionProcessor.EncodeMinimalHpack("GET", longPath);

        // Act: verify HPACK decodes successfully
        var success = HpackDecoder.TryDecode(encoded, out var headers);

        // Assert
        await Assert.That(success).IsTrue();
        await Assert.That(headers).Count().IsEqualTo(3);
        await Assert.That(headers[1]).IsEqualTo((":path", longPath));
        await Assert.That(headers[2]).IsEqualTo((":scheme", "http"));
    }

    [Test]
    public async Task EncodeMinimalHpack_includes_scheme_authority_and_regular_headers()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Host", "example.com"),
            new("Cookie", "sid=abc"),
        };
        var block = Http1ConnectionProcessor.EncodeMinimalHpack("POST", "/chat", headers);

        var table = new HpackDynamicTable();
        var ok = HpackDecoder.TryDecode(block, out var decoded, table);

        await Assert.That(ok).IsTrue();
        await Assert.That(decoded).Contains((":method", "POST"));
        await Assert.That(decoded).Contains((":path", "/chat"));
        await Assert.That(decoded).Contains((":scheme", "http"));
        await Assert.That(decoded).Contains((":authority", "example.com"));
        await Assert.That(decoded).Contains(("cookie", "sid=abc"));
    }
}
