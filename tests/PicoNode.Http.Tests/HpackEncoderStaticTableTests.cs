namespace PicoNode.Http.Tests;

/// <summary>
/// Tests for HpackEncoder static table optimization.
/// Verifies that exact-value and name-only lookups produce correct HPACK output
/// after pre-separating the static table index.
/// </summary>
public sealed class HpackEncoderStaticTableTests
{
    [Test]
    public async Task Encode_ExactValueMatch_Status200_UsesIndexedEncoding()
    {
        // :status 200 is static table index 8 → 0x88 (1-byte indexed)
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { (":status", "200") };

        var encoded = encoder.Encode(headers);

        await Assert.That(encoded.Length).IsEqualTo(1);
        await Assert.That((int)encoded[0]).IsEqualTo(0x88);
    }

    [Test]
    public async Task Encode_ExactValueMatch_MethodGet_UsesIndexedEncoding()
    {
        // :method GET is static table index 2 → 0x82
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { (":method", "GET") };

        var encoded = encoder.Encode(headers);

        await Assert.That(encoded.Length).IsEqualTo(1);
        await Assert.That((int)encoded[0]).IsEqualTo(0x82);
    }

    [Test]
    public async Task Encode_NameOnlyMatch_ContentType_UsesNameReference()
    {
        // content-type exists in static table (index 31, value="")
        // Encoding: 01|11111 (0x3F) + literal value "text/html"
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { ("content-type", "text/html") };

        var encoded = encoder.Encode(headers);

        // Decode and verify
        var decoded = new List<(string, string)>();
        var success = HpackDecoder.TryDecode(encoded, out decoded);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded.Count).IsEqualTo(1);
        await Assert.That(decoded[0].Item1).IsEqualTo("content-type");
        await Assert.That(decoded[0].Item2).IsEqualTo("text/html");
    }

    [Test]
    public async Task Encode_MethodWithNonMatchingValue_UsesLiteralWithNameReference()
    {
        // :method POST exists (index 3), but if we encode :method with "DELETE",
        // it should use :method's index as name reference + literal value
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { (":method", "DELETE") };

        var encoded = encoder.Encode(headers);

        // Decode and verify
        var decoded = new List<(string, string)>();
        var success = HpackDecoder.TryDecode(encoded, out decoded);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded.Count).IsEqualTo(1);
        await Assert.That(decoded[0].Item1).IsEqualTo(":method");
        await Assert.That(decoded[0].Item2).IsEqualTo("DELETE");
    }

    [Test]
    public async Task Encode_RoundTrip_MultipleHeaders_AllVariants()
    {
        // Mixed: exact value, name-only, and custom headers
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)>
        {
            (":status", "200"), // exact: index 8
            ("content-type", "text/html"), // name-only: index 31
            ("x-custom", "hello"), // literal (not in static table)
            (":scheme", "https"), // exact: index 7
            ("cache-control", "no-cache"), // name-only: index 24
        };

        var encoded = encoder.Encode(headers);

        var decoded = new List<(string, string)>();
        var success = HpackDecoder.TryDecode(encoded, out decoded);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded.Count).IsEqualTo(5);
        await Assert.That(decoded[0].Item1).IsEqualTo(":status");
        await Assert.That(decoded[0].Item2).IsEqualTo("200");
        await Assert.That(decoded[4].Item1).IsEqualTo("cache-control");
        await Assert.That(decoded[4].Item2).IsEqualTo("no-cache");
    }

    [Test]
    public async Task Encode_NameOnly_ContentEncoding_UsesCorrectIndex()
    {
        // content-encoding is static table index 26 with empty value
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { ("content-encoding", "gzip") };

        var encoded = encoder.Encode(headers);

        // Decode and verify
        var decoded = new List<(string, string)>();
        var success = HpackDecoder.TryDecode(encoded, out decoded);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded.Count).IsEqualTo(1);
        await Assert.That(decoded[0].Item1).IsEqualTo("content-encoding");
        await Assert.That(decoded[0].Item2).IsEqualTo("gzip");
    }
}
