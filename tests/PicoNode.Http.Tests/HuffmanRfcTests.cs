namespace PicoNode.Http.Tests;

/// <summary>
/// Verifies HuffmanCodec against RFC 7541 Annex B test vectors.
/// </summary>
public sealed class HuffmanRfcTests
{
    /// <summary>
    /// RFC 7541 §C.4 — Response Header Field Representation Examples.
    /// "302" → 0x6402
    /// </summary>
    [Test]
    public async Task Rfc_C4_first_response_single_header()
    {
        // RFC 7541 §C.4.1: Literal Header Field with Indexing
        // The server responds with a 302 status
        // The HPACK encoded header list for this response will be: 0x4882 6402 5885 aec3 771a 4b61 96d0 7abe 9410 54d4 44a8 2005 9504 0b81 66e0 82a6 2d1b ff6e 919d 29ad 1718 63c7 8f0b 97c8 e9ae 82ae 43d3

        // Let's test the simpler: ":status 302" → 0x4803 333032
        // Actually per RFC: ":status: 302" indexed at position 8 → 0x88, but 302 is not 200
        // So it's literal: name index 8, value "302"
        // 0x48 → 0100 1000 = literal with indexing (01), name index = 8 (001000)
        // "302" Huffman-encoded: 0x6402

        var headers = new List<(string, string)> { (":status", "302") };
        var encoder = new HpackEncoder();
        var encoded = encoder.Encode(headers);

        // The encoder should produce: 0x48 0x82 0x64 0x02
        // 0x48 = literal with indexing, name idx 8 (prefix 6 bits)
        // 0x82 = Huffman-encoded "302" length 2
        // 0x64 0x02 = Huffman bytes

        var decoded = new List<(string, string)>();
        HpackDecoder.TryDecode(encoded, out decoded);
        await Assert.That(decoded.Count).IsEqualTo(1);
        await Assert.That(decoded[0].Item1).IsEqualTo(":status");
        await Assert.That(decoded[0].Item2).IsEqualTo("302");
    }

    /// <summary>
    /// Verify Huffman encoding against known-good reference.
    /// "302" → 0x6402 per RFC 7541.
    /// </summary>
    [Test]
    public async Task Huffman_encode_302()
    {
        var input = new byte[] { (byte)'3', (byte)'0', (byte)'2' };
        var encoded = HuffmanCodec.Encode(input);
        // RFC says "302" Huffman = 0x6402
        await Assert.That(encoded.Length).IsEqualTo(2);
        await Assert.That(encoded[0]).IsEqualTo((byte)0x64);
        await Assert.That(encoded[1]).IsEqualTo((byte)0x02);
    }

    /// <summary>
    /// RFC 7541 §C.4.2 — Multiple headers.
    /// ":status 200, content-type text/html,..." → known hex bytes.
    /// We just verify roundtrip correctness.
    /// </summary>
    [Test]
    public async Task Multi_header_response_roundtrip()
    {
        // Input headers may use mixed case; HTTP/2 requires lowercase per RFC 7540 §8.1.2.
        var headers = new List<(string, string)>
        {
            (":status", "200"),
            ("content-type", "text/html; charset=utf-8"),
            ("server", "PicoWeb.Samples.Showcase"),
            ("content-length", "6047"),
            ("X-RateLimit-Limit", "5"),
        };
        var encoder = new HpackEncoder();
        var encoded = encoder.Encode(headers);
        var decoded = new List<(string, string)>();
        HpackDecoder.TryDecode(encoded, out decoded);

        // After encoding, names are normalized to lowercase.
        var expected = new List<(string, string)>
        {
            (":status", "200"),
            ("content-type", "text/html; charset=utf-8"),
            ("server", "PicoWeb.Samples.Showcase"),
            ("content-length", "6047"),
            ("x-ratelimit-limit", "5"),
        };
        for (int i = 0; i < expected.Count; i++)
        {
            await Assert.That(decoded[i].Item1).IsEqualTo(expected[i].Item1);
            await Assert.That(decoded[i].Item2).IsEqualTo(expected[i].Item2);
        }
    }

    /// <summary>
    /// RFC 7541 §5.2 — "www.example.com" Huffman encoding.
    /// Expected: 0xf1e3c2e5f23a6ba0ab90f4ff
    /// </summary>
    [Test]
    public async Task Huffman_encode_www_example_com()
    {
        var input = Encoding.ASCII.GetBytes("www.example.com");
        var encoded = HuffmanCodec.Encode(input);

        // RFC 7541 §C.4.3: The Huffman-encoded literal value for "www.example.com" is 0xf1e3c2e5f23a6ba0ab90f4ff
        var expected = new byte[]
        {
            0xf1,
            0xe3,
            0xc2,
            0xe5,
            0xf2,
            0x3a,
            0x6b,
            0xa0,
            0xab,
            0x90,
            0xf4,
            0xff,
        };

        await Assert.That(encoded.Length).IsEqualTo(expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            await Assert.That(encoded[i]).IsEqualTo(expected[i]);
        }
    }
}
