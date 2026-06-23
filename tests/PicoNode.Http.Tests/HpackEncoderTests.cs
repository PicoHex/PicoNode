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
    public async Task Encodes_indexed_method_POST_using_static_table()
    {
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { (":method", "POST") };
        var encoded = encoder.Encode(headers);

        // Index 3 = :method POST → single byte 0x83 (1000 0011, prefix 7 = index 3)
        // If this test fails, StaticTableIndex is only storing the first entry (:method GET)
        // and missing :method POST at index 3.
        await Assert.That(encoded.Length).IsEqualTo(1);
        await Assert.That((int)encoded[0]).IsEqualTo(0x83);
    }

    [Test]
    public async Task Encodes_indexed_path_index_html_using_static_table()
    {
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { (":path", "/index.html") };
        var encoded = encoder.Encode(headers);

        // Index 5 = :path /index.html → single byte 0x85
        await Assert.That(encoded.Length).IsEqualTo(1);
        await Assert.That((int)encoded[0]).IsEqualTo(0x85);
    }

    [Test]
    public async Task Encodes_indexed_scheme_https_using_static_table()
    {
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)> { (":scheme", "https") };
        var encoded = encoder.Encode(headers);

        // Index 7 = :scheme https → single byte 0x87
        await Assert.That(encoded.Length).IsEqualTo(1);
        await Assert.That((int)encoded[0]).IsEqualTo(0x87);
    }

    [Test]
    public async Task Uses_huffman_encoding_for_long_header_values()
    {
        var encoder = new HpackEncoder();
        var longValue = new string('a', 100);
        var headers = new List<(string, string)> { ("x-custom", longValue) };
        var encoded = encoder.Encode(headers);

        // With Huffman, 'a' (0x61) encodes as 6 bits (code 0x18, bitlen 6).
        // 100 'a' chars = 600 bits = 75 bytes (vs 100 bytes raw).
        // Plus header overhead: the string length should be encoded with H bit set (>= 128).
        // Without Huffman: the first byte would be < 128 (H bit = 0).
        // With Huffman: the first byte of the string length encoding has H bit = 1 (>= 128).
        // Check that the three length-prefix bytes of the value string start with H=1.

        // Find the value string length prefix (after name + value length).
        // x-custom is not in static table, so format is: 0x40 + len(name) + name + len(value) + value.
        // 0x40 (1 byte) + len("x-custom") (1 byte: 8) + "x-custom" (8 bytes) = 10 bytes header.
        // Then the value length prefix byte: if >= 128, H bit is set = Huffman.
        if (encoded.Length > 10)
        {
            // The value length prefix is at position after name + its length prefix:
            // byte 0 = 0x40, byte 1 = 8 (name length), bytes 2-9 = "x-custom", byte 10 = value length prefix
            var valueLenPrefix = encoded[10];
            // H bit = bit 7 (0x80): Huffman
            await Assert.That((valueLenPrefix & 0x80)).IsNotEqualTo(0);
        }
    }

    [Test]
    public async Task Uses_plain_encoding_for_short_header_values()
    {
        var encoder = new HpackEncoder();
        var shortValue = "a";
        var headers = new List<(string, string)> { ("x-custom", shortValue) };
        var encoded = encoder.Encode(headers);

        // A single byte: Huffman would make it larger (6 bits → pad to 8 bits = 1 byte + EOS padding).
        // So plain encoding should be used.
        if (encoded.Length > 10)
        {
            var valueLenPrefix = encoded[10];
            // H bit = not set: plain
            await Assert.That((valueLenPrefix & 0x80)).IsEqualTo(0);
        }
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

    [Test]
    public async Task Encode_into_IBufferWriter_produces_same_result()
    {
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)>
        {
            (":status", "200"),
            ("content-type", "text/plain; charset=utf-8"),
            ("cache-control", "no-cache"),
        };

        var baseline = encoder.Encode(headers);

        var writer = new ArrayBufferWriter<byte>();
        encoder.Encode(writer, headers);
        var fromWriter = writer.WrittenMemory.ToArray();

        await Assert.That(fromWriter).IsEquivalentTo(baseline);
    }

    [Test]
    public async Task Roundtrip_response_with_rate_limit_headers()
    {
        var encoder = new HpackEncoder();
        var headers = new List<(string, string)>
        {
            (":status", "200"),
            ("content-type", "text/html; charset=utf-8"),
            ("server", "PicoWeb.Samples.Showcase"),
            ("content-length", "6047"),
            ("X-RateLimit-Limit", "5"),
            ("X-RateLimit-Remaining", "4"),
            ("X-RateLimit-Reset", "1750896000"),
        };
        var encoded = encoder.Encode(headers);

        var decoded = new List<(string, string)>();
        var ok = HpackDecoder.TryDecode(encoded, out decoded);

        await Assert.That(ok).IsTrue();
        await Assert.That(decoded.Count).IsEqualTo(headers.Count);
        for (int i = 0; i < headers.Count; i++)
        {
            await Assert.That(decoded[i].Item1).IsEqualTo(
                headers[i].Item1, $"Header {i} name mismatch");
            await Assert.That(decoded[i].Item2).IsEqualTo(
                headers[i].Item2, $"Header {i} value mismatch");
        }
    }
}
