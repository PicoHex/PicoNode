namespace PicoNode.Web.Tests;

public sealed class WebResultsTests
{
    [Test]
    public async Task Text_creates_response_with_text_content_type()
    {
        var response = WebResults.Text(200, "hello", "OK");

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.ReasonPhrase).IsEqualTo("OK");
        await Assert.That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Content-Type", "text/plain; charset=utf-8"));
        await Assert.That(Encoding.UTF8.GetString(response.Body.Span)).IsEqualTo("hello");
    }

    [Test]
    public async Task Json_creates_response_with_json_content_type()
    {
        var response = WebResults.Json(200, """{"ok":true}""", "OK");

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers)
            .Contains(
                new KeyValuePair<string, string>("Content-Type", "application/json; charset=utf-8")
            );
        await Assert.That(Encoding.UTF8.GetString(response.Body.Span)).IsEqualTo("""{"ok":true}""");
    }

    [Test]
    public async Task Bytes_creates_response_with_specified_content_type()
    {
        var body = new byte[] { 0x01, 0x02, 0x03 };
        var response = WebResults.Bytes(200, body, "application/octet-stream", "OK");

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers)
            .Contains(
                new KeyValuePair<string, string>("Content-Type", "application/octet-stream")
            );
        await Assert.That(response.Body.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Empty_creates_response_with_no_body()
    {
        var response = WebResults.Empty(204, "No Content");

        await Assert.That(response.StatusCode).IsEqualTo(204);
        await Assert.That(response.ReasonPhrase).IsEqualTo("No Content");
        await Assert.That(response.Body.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Redirect_creates_302_by_default()
    {
        var response = WebResults.Redirect("/new-location");

        await Assert.That(response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Location", "/new-location"));
    }

    [Test]
    public async Task Redirect_creates_301_when_permanent()
    {
        var response = WebResults.Redirect("/new-location", permanent: true);

        await Assert.That(response.StatusCode).IsEqualTo(301);
        await Assert.That(response.ReasonPhrase).IsEqualTo("Moved Permanently");
    }
}
