
namespace PicoNode.Web.Tests;

public sealed class MultipartBufferedReaderTests
{
    [Test]
    public async Task ReadLine_reads_until_crlf()
    {
        var data = "header: value\r\nnext-line"u8.ToArray();
        using var reader = new MultipartBufferedReader(new MemoryStream(data), 1024);
        var line = await reader.ReadLineAsync();
        await Assert.That(line).IsNotNull();
        await Assert.That(Encoding.UTF8.GetString(line!)).IsEqualTo("header: value");
    }

    [Test]
    public async Task ReadLine_returns_null_at_end()
    {
        var data = "line1\r\n"u8.ToArray();
        using var reader = new MultipartBufferedReader(new MemoryStream(data), 1024);
        await reader.ReadLineAsync();
        var line2 = await reader.ReadLineAsync();
        await Assert.That(line2).IsNull();
    }

    [Test]
    public async Task ReadUntilBoundary_returns_content()
    {
        var name = "bound"u8.ToArray();
        var data = "content data\r\n--bound\r\n"u8.ToArray();
        using var reader = new MultipartBufferedReader(new MemoryStream(data), 1024);
        var content = await reader.ReadUntilBoundaryAsync(name);
        await Assert.That(content).IsNotNull();
        await Assert.That(Encoding.UTF8.GetString(content!)).IsEqualTo("content data");
    }
}
