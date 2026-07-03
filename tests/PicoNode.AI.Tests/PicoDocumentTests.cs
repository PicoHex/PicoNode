namespace PicoNode.AI.Tests;

public class PicoDocumentTests
{
    [Test]
    public async Task Parse_Object_ReadProperty()
    {
        var doc = PicoDocument.Parse("""{"type":"delta","content":"Hello"}"""u8.ToArray());
        await Assert.That(doc.RootElement["type"].GetString()).IsEqualTo("delta");
        await Assert.That(doc.RootElement["content"].GetString()).IsEqualTo("Hello");
    }

    [Test]
    public async Task TryGetProperty_Exists_ReturnsTrue()
    {
        var doc = PicoDocument.Parse("""{"required":["path","name"]}"""u8.ToArray());
        var ok = doc.RootElement.TryGetProperty("required", out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(val.GetArrayLength()).IsEqualTo(2);
        await Assert.That(val[0].GetString()).IsEqualTo("path");
    }

    [Test]
    public async Task TryGetProperty_Missing_ReturnsFalse()
    {
        var doc = PicoDocument.Parse("""{"name":"test"}"""u8.ToArray());
        var ok = doc.RootElement.TryGetProperty("required", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task EnumerateArray_ReturnsElements()
    {
        var doc = PicoDocument.Parse("""["a","b","c"]"""u8.ToArray());
        var arr = doc.RootElement;
        await Assert.That(arr.GetArrayLength()).IsEqualTo(3);
        var items = new List<string>();
        for (int i = 0; i < arr.GetArrayLength(); i++)
            items.Add(arr[i].GetString()!);
        await Assert.That(items[0]).IsEqualTo("a");
        await Assert.That(items[2]).IsEqualTo("c");
    }

    [Test]
    public async Task IsValid_ValidJson_ReturnsTrue()
    {
        await Assert.That(PicoDocument.IsValid("{}"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("""{"a":1}"""u8)).IsTrue();
    }

    [Test]
    public async Task IsValid_InvalidJson_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("{bad}"u8)).IsFalse();
    }

    [Test]
    public async Task GetRawValue_ReturnsUnescapedText()
    {
        var doc = PicoDocument.Parse("""{"name":"Alice"}"""u8.ToArray());
        var raw = doc.RootElement["name"].GetRawValue();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("Alice");
    }
}
