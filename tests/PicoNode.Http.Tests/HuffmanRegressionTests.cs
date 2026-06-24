namespace PicoNode.Http.Tests;

public sealed class HuffmanRegressionTests
{
    /// <summary>
    /// Round-trip via HPACK: head header → encode → decode → assert match.
    /// Uses a header with X-RateLimit values that were known to trigger compression error.
    /// </summary>
    [Test]
    public async Task Roundtrip_all_headers_survive()
    {
        var headers = new List<(string, string)>
        {
            (":status", "200"),
            ("content-type", "text/html; charset=utf-8"),
            ("server", "PicoWeb.Samples.Showcase"),
            ("X-RateLimit-Limit", "5"),
            ("X-RateLimit-Remaining", "3"),
            ("X-RateLimit-Reset", "1750896000"),
            ("cache-control", "no-cache"),
            ("content-length", "6047"),
        };

        var encoder = new HpackEncoder();
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

    /// <summary>
    /// Regression: the server's response headers for a page load must roundtrip.
    /// </summary>
    [Test]
    public async Task Page_response_headers_roundtrip()
    {
        var headers = new List<(string, string)>
        {
            (":status", "200"),
            ("content-type", "text/html; charset=utf-8"),
            ("server", "PicoWeb.Samples.Showcase"),
            ("X-RateLimit-Limit", "5"),
            ("X-RateLimit-Remaining", "4"),
            ("X-RateLimit-Reset", "1750896000"),
        };

        var encoder = new HpackEncoder();
        var encoded = encoder.Encode(headers);
        var decoded = new List<(string, string)>();
        HpackDecoder.TryDecode(encoded, out decoded);

        await Assert.That(decoded.Count).IsEqualTo(6);
    }
}
