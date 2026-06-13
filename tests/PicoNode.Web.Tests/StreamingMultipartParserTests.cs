
namespace PicoNode.Web.Tests;

public sealed class StreamingMultipartParserTests
{
    [Test]
    public async Task ParseAsync_parses_single_text_field()
    {
        var data =
            "--b\r\nContent-Disposition: form-data; name=\"user\"\r\n\r\nalice\r\n--b--\r\n"u8.ToArray();
        using var stream = new MemoryStream(data);

        var result = await StreamingMultipartParser.ParseAsync(stream, "b");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Name).IsEqualTo("user");
        await Assert.That(result.Fields[0].Value).IsEqualTo("alice");
        await Assert.That(result.Files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseAsync_parses_file()
    {
        var data =
            "--b\r\nContent-Disposition: form-data; name=\"doc\"; filename=\"readme.txt\"\r\nContent-Type: text/plain\r\n\r\nhello world\r\n--b--\r\n"u8.ToArray();
        using var stream = new MemoryStream(data);

        var result = await StreamingMultipartParser.ParseAsync(stream, "b");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].Name).IsEqualTo("doc");
        await Assert.That(result.Files[0].FileName).IsEqualTo("readme.txt");
        await Assert.That(result.Files[0].ContentType).IsEqualTo("text/plain");
        await Assert.That(result.Files[0].Length).IsEqualTo(11);
    }

    [Test]
    public async Task ParseAsync_parses_mixed_field_and_file()
    {
        var data =
            "--b\r\nContent-Disposition: form-data; name=\"title\"\r\n\r\nhello\r\n--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"data.bin\"\r\n\r\nbinary\r\n--b--\r\n"u8.ToArray();
        using var stream = new MemoryStream(data);

        var result = await StreamingMultipartParser.ParseAsync(stream, "b");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Name).IsEqualTo("title");
        await Assert.That(result.Fields[0].Value).IsEqualTo("hello");
        await Assert.That(result.Files.Count).IsEqualTo(1);
        await Assert.That(result.Files[0].FileName).IsEqualTo("data.bin");
    }
}
