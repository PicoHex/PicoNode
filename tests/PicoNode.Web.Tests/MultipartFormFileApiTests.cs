namespace PicoNode.Web.Tests;

public sealed class MultipartFormFileApiTests
{
    [Test]
    public async Task OpenReadStream_returns_stream_with_file_content()
    {
        var data = "hello file content"u8.ToArray();
        var file = new MultipartFormFile("doc", "readme.txt", "text/plain", data);

        await Assert.That(file.Name).IsEqualTo("doc");
        await Assert.That(file.FileName).IsEqualTo("readme.txt");
        await Assert.That(file.ContentType).IsEqualTo("text/plain");
        await Assert.That(file.Length).IsEqualTo(data.Length);

        using var stream = file.OpenReadStream();
        var buffer = new byte[data.Length];
        var bytesRead = await stream.ReadAsync(buffer);
        await Assert.That(bytesRead).IsEqualTo(data.Length);
        await Assert.That(buffer).IsEquivalentTo(data);
    }
}
