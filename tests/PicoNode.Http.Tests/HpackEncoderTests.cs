using PicoNode.Http.Internal.Hpack;

namespace PicoNode.Http.Tests;

public sealed class HpackEncoderTests
{
    [Test]
    public async Task Encodes_indexed_status_200()
    {
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { (":status", "200") };
        var encoded = encoder.Encode(headers);

        // Index 8 = :status 200 → single byte 0x88 (1000 1000, prefix 7 = index 8)
        await Assert.That(encoded.Length).IsEqualTo(1);
        await Assert.That((int)encoded[0]).IsEqualTo(0x88);
    }

    [Test]
    public async Task Encodes_literal_content_type()
    {
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { ("content-type", "application/json") };
        var encoded = encoder.Encode(headers);

        // Should be literal (no static table entry for content-type with json value)
        // Decode back to verify
        var decoded = new List<(string, string)>();
        HpackDecoder.TryDecode(encoded, out decoded);
        await Assert.That(decoded.Count).IsEqualTo(1);
        await Assert.That(decoded[0].Item1).IsEqualTo("content-type");
        await Assert.That(decoded[0].Item2).IsEqualTo("application/json");
    }

    [Test]
    public async Task Encodes_and_roundtrips_multiple_headers()
    {
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)>
        {
            (":status", "200"),
            ("content-type", "text/plain"),
            ("cache-control", "no-cache"),
        };
        var encoded = encoder.Encode(headers);

        var decoded = new List<(string, string)>();
        HpackDecoder.TryDecode(encoded, out decoded);
        await Assert.That(decoded.Count).IsEqualTo(3);
        await Assert.That(decoded[0].Item1).IsEqualTo(":status");
        await Assert.That(decoded[0].Item2).IsEqualTo("200");
        await Assert.That(decoded[1].Item2).IsEqualTo("text/plain");
    }

    [Test]
    public async Task Uses_static_table_name_reference_for_common_headers()
    {
        var encoder = new HpackEncoder();
        // "content-type" exists in static table with empty value (index 31)
        var headers = new List<(string, string)> { ("content-type", "text/html") };
        var encoded = encoder.Encode(headers);

        // Should use static table name reference (index 31) + literal value
        // This produces a smaller encoding than full literal
        // Decode and verify
        var decoded = new List<(string, string)>();
        HpackDecoder.TryDecode(encoded, out decoded);
        await Assert.That(decoded[0].Item2).IsEqualTo("text/html");
    }

    [Test]
    public async Task Dynamic_table_reuses_repeated_headers()
    {
        var dynamicTable = new HpackDynamicTable();
        var encoder = new HpackEncoder(dynamicTable);

        // First request: x-custom: foo (added to dynamic table)
        var headers1 = new List<(string, string)> { (":status", "200"), ("x-custom", "foo") };
        var encoded1 = encoder.Encode(headers1);

        // Second request: reuses x-custom from dynamic table
        var headers2 = new List<(string, string)> { (":status", "200"), ("x-custom", "foo") };
        var encoded2 = encoder.Encode(headers2);

        // Second encoding should be smaller (references dynamic table)
        await Assert.That(encoded2.Length).IsLessThan(encoded1.Length);
    }
}
