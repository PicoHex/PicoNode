namespace PicoNode.Web.Tests;

public sealed class MultipartFormDataParserAsyncTests
{
    [Test]
    public async Task ParseAsync_parses_multipart()
    {
        var body =
            "--bound\r\nContent-Disposition: form-data; name=\"field1\"\r\n\r\nvalue1\r\n--bound--\r\n"u8.ToArray();
        var request = new HttpRequest
        {
            Method = "POST",
            Target = "/upload",
            Version = HttpVersion.Http11,
            HeaderFields =
            [
                new("Content-Type", "multipart/form-data; boundary=bound"),
                new("Content-Length", body.Length.ToString()),
                new("Host", "localhost"),
            ],
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "multipart/form-data; boundary=bound",
                ["Content-Length"] = body.Length.ToString(),
                ["Host"] = "localhost",
            },
            BodyStream = new MemoryStream(body, writable: false),
        };

        var result = await MultipartFormDataParser.ParseAsync(request);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Fields.Count).IsEqualTo(1);
        await Assert.That(result.Fields[0].Name).IsEqualTo("field1");
        await Assert.That(result.Fields[0].Value).IsEqualTo("value1");
    }
}
