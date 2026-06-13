namespace PicoNode.Web.Tests;

public sealed class StreamingPartReaderTests
{
    [Test]
    public async Task ReadPart_reads_headers_and_content()
    {
        var data =
            "--bound\r\nContent-Disposition: form-data; name=\"field1\"\r\n\r\nvalue1\r\n--bound--\r\n"u8.ToArray();
        using var reader = new MultipartBufferedReader(new MemoryStream(data), 1024);
        var boundary = "bound"u8.ToArray();

        // Skip first boundary
        await reader.ReadUntilBoundaryAsync(boundary);

        // Read first part
        var part = await MultipartPartReader.ReadPartAsync(reader, boundary);

        await Assert.That(part).IsNotNull();
        await Assert.That(part!.Headers.ContainsKey("Content-Disposition")).IsTrue();
        await Assert.That(part.Content).IsNotNull();
        await Assert.That(Encoding.UTF8.GetString(part.Content!)).IsEqualTo("value1");
    }

    [Test]
    public async Task ReadPart_parses_headers()
    {
        var data =
            "--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"doc.txt\"\r\nContent-Type: text/plain\r\n\r\nhello\r\n--b--\r\n"u8.ToArray();
        using var reader = new MultipartBufferedReader(new MemoryStream(data), 1024);
        var boundary = "b"u8.ToArray();

        await reader.ReadUntilBoundaryAsync(boundary);
        var part = await MultipartPartReader.ReadPartAsync(reader, boundary);

        await Assert.That(part).IsNotNull();
        var disp = part!.Headers["Content-Disposition"];
        await Assert.That(disp).Contains("filename=\"doc.txt\"");
        await Assert.That(part.Content).IsNotNull();
        await Assert.That(Encoding.UTF8.GetString(part.Content!)).IsEqualTo("hello");
    }
}
