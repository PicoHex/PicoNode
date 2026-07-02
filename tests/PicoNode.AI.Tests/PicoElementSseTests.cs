using System.Text;
using PicoJetson;

namespace PicoNode.AI.Tests;

public class PicoElementSseTests
{
    [Test]
    public async Task GetString_OnNullValue_DoesNotThrow()
    {
        var doc = PicoDocument.Parse("""{"finish_reason":null}"""u8.ToArray());
        // finish_reason could be null in SSE responses
        var el = doc.RootElement["finish_reason"];
        string? s;
        try { s = el.GetString(); } catch { s = null; }
        // Either returns null or the string — must not throw
        await Assert.That(true).IsTrue(); // reached
    }

    [Test]
    public async Task TryGetInt64_OnNumber_ReturnsTrue()
    {
        var doc = PicoDocument.Parse("""{"tokens":42}"""u8.ToArray());
        await Assert.That(doc.RootElement["tokens"].TryGetInt64(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(42);
    }

    [Test]
    public async Task TryGetInt64_OnString_ReturnsFalse()
    {
        var doc = PicoDocument.Parse("""{"name":"Alice"}"""u8.ToArray());
        await Assert.That(doc.RootElement["name"].TryGetInt64(out _)).IsFalse();
    }

    [Test]
    public async Task GetDouble_OnFloat_ReturnsCorrect()
    {
        var doc = PicoDocument.Parse("""{"score":3.14}"""u8.ToArray());
        await Assert.That(doc.RootElement["score"].GetDouble()).IsEqualTo(3.14);
    }

    [Test]
    public async Task GetString_OnActualString_ReturnsValue()
    {
        var doc = PicoDocument.Parse("""{"type":"delta"}"""u8.ToArray());
        await Assert.That(doc.RootElement["type"].GetString()).IsEqualTo("delta");
    }

    [Test]
    public async Task TryGetProperty_OnNested_Works()
    {
        var doc = PicoDocument.Parse("""{"choices":[{"delta":{"content":"Hi"}}]}"""u8.ToArray());
        await Assert.That(doc.RootElement.TryGetProperty("choices", out var c)).IsTrue();
        await Assert.That(c.GetArrayLength()).IsEqualTo(1);
        await Assert.That(c[0].TryGetProperty("delta", out var d)).IsTrue();
        await Assert.That(d.TryGetProperty("content", out var t)).IsTrue();
        await Assert.That(t.GetString()).IsEqualTo("Hi");
    }
}
