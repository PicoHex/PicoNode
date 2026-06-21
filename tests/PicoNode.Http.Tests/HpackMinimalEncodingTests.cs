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
        await Assert.That(headers).Count().IsEqualTo(2);
        await Assert.That(headers[0]).IsEqualTo((":method", "GET"));
        await Assert.That(headers[1]).IsEqualTo((":path", "/"));
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
        await Assert.That(headers).Count().IsEqualTo(2);
        await Assert.That(headers[0]).IsEqualTo((":method", "POST"));
        await Assert.That(headers[1]).IsEqualTo((":path", "/users"));
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
        await Assert.That(headers).Count().IsEqualTo(2);
        await Assert.That(headers[0]).IsEqualTo((":method", "PUT"));
        await Assert.That(headers[1]).IsEqualTo((":path", "/api/users/123"));
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
        await Assert.That(headers).Count().IsEqualTo(2);
        await Assert.That(headers[1]).IsEqualTo((":path", longPath));
    }
}
