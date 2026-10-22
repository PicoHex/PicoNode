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
    /// Huffman round-trip must preserve multi-byte UTF-8 sequences.
    /// "café" = bytes [63 61 66 C3 A9]. The decoder must accumulate bytes
    /// and decode via UTF-8, not cast each byte to char.
    /// </summary>
    [Test]
    public async Task Huffman_roundtrip_preserves_multibyte_utf8()
    {
        var input = Encoding.UTF8.GetBytes("café");
        var encoded = HuffmanCodec.Encode(input);
        var success = HuffmanCodec.TryDecode(encoded, out var decoded);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded).IsEqualTo("café");
    }

    /// <summary>
    /// Huffman round-trip of a larger UTF-8 string with CJK characters.
    /// "日本" = bytes [E6 97 A5 E6 9C AC].
    /// </summary>
    [Test]
    public async Task Huffman_roundtrip_preserves_cjk_utf8()
    {
        var input = Encoding.UTF8.GetBytes("日本");
        var encoded = HuffmanCodec.Encode(input);
        var success = HuffmanCodec.TryDecode(encoded, out var decoded);

        await Assert.That(success).IsTrue();
        await Assert.That(decoded).IsEqualTo("日本");
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

    /// <summary>
    /// RFC 7541 Appendix B, transcribed here independently of the codec under test:
    /// (code right-justified, bit-length) for symbols 0..256, where 256 is EOS.
    /// </summary>
    private static readonly (uint Code, int BitLength)[] RfcAppendixB =
    [
        (0x1FF8, 13),
        (0x7FFFD8, 23),
        (0xFFFFFE2, 28),
        (0xFFFFFE3, 28),
        (0xFFFFFE4, 28),
        (0xFFFFFE5, 28),
        (0xFFFFFE6, 28),
        (0xFFFFFE7, 28),
        (0xFFFFFE8, 28),
        (0xFFFFEA, 24),
        (0x3FFFFFFC, 30),
        (0xFFFFFE9, 28),
        (0xFFFFFEA, 28),
        (0x3FFFFFFD, 30),
        (0xFFFFFEB, 28),
        (0xFFFFFEC, 28),
        (0xFFFFFED, 28),
        (0xFFFFFEE, 28),
        (0xFFFFFEF, 28),
        (0xFFFFFF0, 28),
        (0xFFFFFF1, 28),
        (0xFFFFFF2, 28),
        (0x3FFFFFFE, 30),
        (0xFFFFFF3, 28),
        (0xFFFFFF4, 28),
        (0xFFFFFF5, 28),
        (0xFFFFFF6, 28),
        (0xFFFFFF7, 28),
        (0xFFFFFF8, 28),
        (0xFFFFFF9, 28),
        (0xFFFFFFA, 28),
        (0xFFFFFFB, 28),
        (0x14, 6),
        (0x3F8, 10),
        (0x3F9, 10),
        (0xFFA, 12),
        (0x1FF9, 13),
        (0x15, 6),
        (0xF8, 8),
        (0x7FA, 11),
        (0x3FA, 10),
        (0x3FB, 10),
        (0xF9, 8),
        (0x7FB, 11),
        (0xFA, 8),
        (0x16, 6),
        (0x17, 6),
        (0x18, 6),
        (0x0, 5),
        (0x1, 5),
        (0x2, 5),
        (0x19, 6),
        (0x1A, 6),
        (0x1B, 6),
        (0x1C, 6),
        (0x1D, 6),
        (0x1E, 6),
        (0x1F, 6),
        (0x5C, 7),
        (0xFB, 8),
        (0x7FFC, 15),
        (0x20, 6),
        (0xFFB, 12),
        (0x3FC, 10),
        (0x1FFA, 13),
        (0x21, 6),
        (0x5D, 7),
        (0x5E, 7),
        (0x5F, 7),
        (0x60, 7),
        (0x61, 7),
        (0x62, 7),
        (0x63, 7),
        (0x64, 7),
        (0x65, 7),
        (0x66, 7),
        (0x67, 7),
        (0x68, 7),
        (0x69, 7),
        (0x6A, 7),
        (0x6B, 7),
        (0x6C, 7),
        (0x6D, 7),
        (0x6E, 7),
        (0x6F, 7),
        (0x70, 7),
        (0x71, 7),
        (0x72, 7),
        (0xFC, 8),
        (0x73, 7),
        (0xFD, 8),
        (0x1FFB, 13),
        (0x7FFF0, 19),
        (0x1FFC, 13),
        (0x3FFC, 14),
        (0x22, 6),
        (0x7FFD, 15),
        (0x3, 5),
        (0x23, 6),
        (0x4, 5),
        (0x24, 6),
        (0x5, 5),
        (0x25, 6),
        (0x26, 6),
        (0x27, 6),
        (0x6, 5),
        (0x74, 7),
        (0x75, 7),
        (0x28, 6),
        (0x29, 6),
        (0x2A, 6),
        (0x7, 5),
        (0x2B, 6),
        (0x76, 7),
        (0x2C, 6),
        (0x8, 5),
        (0x9, 5),
        (0x2D, 6),
        (0x77, 7),
        (0x78, 7),
        (0x79, 7),
        (0x7A, 7),
        (0x7B, 7),
        (0x7FFE, 15),
        (0x7FC, 11),
        (0x3FFD, 14),
        (0x1FFD, 13),
        (0xFFFFFFC, 28),
        (0xFFFE6, 20),
        (0x3FFFD2, 22),
        (0xFFFE7, 20),
        (0xFFFE8, 20),
        (0x3FFFD3, 22),
        (0x3FFFD4, 22),
        (0x3FFFD5, 22),
        (0x7FFFD9, 23),
        (0x3FFFD6, 22),
        (0x7FFFDA, 23),
        (0x7FFFDB, 23),
        (0x7FFFDC, 23),
        (0x7FFFDD, 23),
        (0x7FFFDE, 23),
        (0xFFFFEB, 24),
        (0x7FFFDF, 23),
        (0xFFFFEC, 24),
        (0xFFFFED, 24),
        (0x3FFFD7, 22),
        (0x7FFFE0, 23),
        (0xFFFFEE, 24),
        (0x7FFFE1, 23),
        (0x7FFFE2, 23),
        (0x7FFFE3, 23),
        (0x7FFFE4, 23),
        (0x1FFFDC, 21),
        (0x3FFFD8, 22),
        (0x7FFFE5, 23),
        (0x3FFFD9, 22),
        (0x7FFFE6, 23),
        (0x7FFFE7, 23),
        (0xFFFFEF, 24),
        (0x3FFFDA, 22),
        (0x1FFFDD, 21),
        (0xFFFE9, 20),
        (0x3FFFDB, 22),
        (0x3FFFDC, 22),
        (0x7FFFE8, 23),
        (0x7FFFE9, 23),
        (0x1FFFDE, 21),
        (0x7FFFEA, 23),
        (0x3FFFDD, 22),
        (0x3FFFDE, 22),
        (0xFFFFF0, 24),
        (0x1FFFDF, 21),
        (0x3FFFDF, 22),
        (0x7FFFEB, 23),
        (0x7FFFEC, 23),
        (0x1FFFE0, 21),
        (0x1FFFE1, 21),
        (0x3FFFE0, 22),
        (0x1FFFE2, 21),
        (0x7FFFED, 23),
        (0x3FFFE1, 22),
        (0x7FFFEE, 23),
        (0x7FFFEF, 23),
        (0xFFFEA, 20),
        (0x3FFFE2, 22),
        (0x3FFFE3, 22),
        (0x3FFFE4, 22),
        (0x7FFFF0, 23),
        (0x3FFFE5, 22),
        (0x3FFFE6, 22),
        (0x7FFFF1, 23),
        (0x3FFFFE0, 26),
        (0x3FFFFE1, 26),
        (0xFFFEB, 20),
        (0x7FFF1, 19),
        (0x3FFFE7, 22),
        (0x7FFFF2, 23),
        (0x3FFFE8, 22),
        (0x1FFFFEC, 25),
        (0x3FFFFE2, 26),
        (0x3FFFFE3, 26),
        (0x3FFFFE4, 26),
        (0x7FFFFDE, 27),
        (0x7FFFFDF, 27),
        (0x3FFFFE5, 26),
        (0xFFFFF1, 24),
        (0x1FFFFED, 25),
        (0x7FFF2, 19),
        (0x1FFFE3, 21),
        (0x3FFFFE6, 26),
        (0x7FFFFE0, 27),
        (0x7FFFFE1, 27),
        (0x3FFFFE7, 26),
        (0x7FFFFE2, 27),
        (0xFFFFF2, 24),
        (0x1FFFE4, 21),
        (0x1FFFE5, 21),
        (0x3FFFFE8, 26),
        (0x3FFFFE9, 26),
        (0xFFFFFFD, 28),
        (0x7FFFFE3, 27),
        (0x7FFFFE4, 27),
        (0x7FFFFE5, 27),
        (0xFFFEC, 20),
        (0xFFFFF3, 24),
        (0xFFFED, 20),
        (0x1FFFE6, 21),
        (0x3FFFE9, 22),
        (0x1FFFE7, 21),
        (0x1FFFE8, 21),
        (0x7FFFF3, 23),
        (0x3FFFEA, 22),
        (0x3FFFEB, 22),
        (0x1FFFFEE, 25),
        (0x1FFFFEF, 25),
        (0xFFFFF4, 24),
        (0xFFFFF5, 24),
        (0x3FFFFEA, 26),
        (0x7FFFF4, 23),
        (0x3FFFFEB, 26),
        (0x7FFFFE6, 27),
        (0x3FFFFEC, 26),
        (0x3FFFFED, 26),
        (0x7FFFFE7, 27),
        (0x7FFFFE8, 27),
        (0x7FFFFE9, 27),
        (0x7FFFFEA, 27),
        (0x7FFFFEB, 27),
        (0xFFFFFFE, 28),
        (0x7FFFFEC, 27),
        (0x7FFFFED, 27),
        (0x7FFFFEE, 27),
        (0x7FFFFEF, 27),
        (0x7FFFFF0, 27),
        (0x3FFFFEE, 26),
        (0x3FFFFFFF, 30),
    ];

    /// <summary>
    /// Emits the byte stream a conforming HTTP/2 peer produces for one symbol: its
    /// Appendix B code, then 1-bit padding to the next byte boundary (RFC 7541 §5.2).
    /// </summary>
    private static byte[] ConformingEncode(byte symbol)
    {
        var (code, bitLength) = RfcAppendixB[symbol];
        var pad = (8 - (bitLength & 7)) & 7;
        var padded = (code << pad) | ((1u << pad) - 1);
        var length = (bitLength + pad) / 8;
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(padded >> (8 * (length - 1 - i)));
        return bytes;
    }

    /// <summary>
    /// RFC 7541 Appendix B — the whole 28-bit range must match the RFC. 29 of those
    /// code words lost their leading F nibble, so 0xDC encoded to 0F FF FF DF where a
    /// conforming peer produces FF FF FF DF.
    /// </summary>
    [Test]
    public async Task Huffman_encode_covers_the_28_bit_range()
    {
        // RFC 7541 Appendix B: every one of these is a 28-bit code.
        var expected = new (byte Symbol, byte[] Encoded)[]
        {
            (0x02, [0xFF, 0xFF, 0xFE, 0x2F]),
            (0x7F, [0xFF, 0xFF, 0xFF, 0xCF]),
            (0xDC, [0xFF, 0xFF, 0xFF, 0xDF]),
            (0xF9, [0xFF, 0xFF, 0xFF, 0xEF]),
        };
        foreach (var (symbol, bytes) in expected)
            await Assert.That(HuffmanCodec.Encode([symbol])).IsEquivalentTo(bytes);
    }

    /// <summary>
    /// A conforming peer encodes every byte with the RFC codes. The decoder must accept
    /// all of them: 29 of these streams used to index the tree at -1 and throw
    /// IndexOutOfRangeException, which takes the whole HTTP/2 connection down.
    /// </summary>
    [Test]
    public async Task Huffman_decode_accepts_conforming_peer_output_for_every_symbol()
    {
        for (var symbol = 0; symbol < 256; symbol++)
        {
            var encoded = ConformingEncode((byte)symbol);
            var ok = HuffmanCodec.TryDecode(encoded, out var decoded);
            await Assert.That(ok).IsTrue().Because($"symbol 0x{symbol:X2} must decode");
            await Assert.That(decoded).IsEqualTo(Encoding.UTF8.GetString([(byte)symbol]));
        }
    }

    /// <summary>
    /// Damaged streams must be rejected, never thrown out of: TryDecode's contract is
    /// "false for anything invalid", because a decoder exception aborts the connection.
    /// </summary>
    [Test]
    public async Task Huffman_decode_never_throws_on_dead_end_paths()
    {
        // Twenty-nine 1-bits then a 0 is the 30-bit code of 0x16; the trailing 11 is padding.
        var threw = false;
        var ok = true;
        try
        {
            ok = HuffmanCodec.TryDecode([0xFF, 0xFF, 0xFF, 0xFB], out _);
        }
        catch (Exception)
        {
            threw = true;
        }
        await Assert.That(threw).IsFalse();
        await Assert.That(ok).IsTrue();

        // EOS (30 one-bits) may never appear in a stream — reject it, do not throw.
        await Assert.That(HuffmanCodec.TryDecode([0xFF, 0xFF, 0xFF, 0xFF], out _)).IsFalse();

        // A 28-bit code the table could not place walked off the tree; it must decode
        // to its symbol rather than throw.
        await Assert.That(HuffmanCodec.TryDecode([0xFF, 0xFF, 0xFE, 0x2F], out var low)).IsTrue();
        await Assert.That(low).IsEqualTo(Encoding.UTF8.GetString([(byte)0x02]));
    }
}
