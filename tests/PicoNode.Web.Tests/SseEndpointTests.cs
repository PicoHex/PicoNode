namespace PicoNode.Web.Tests;

public sealed class SseEndpointTests
{
    [Test]
    public async Task SseConnection_write_json_formats_correctly()
    {
        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);

        await sse.WriteJsonAsync("{\"message\":\"hello\"}", CancellationToken.None);

        // Read pipe without completing — just check the write
        var result = await pipe.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(result.Buffer.FirstSpan);
        pipe.Reader.AdvanceTo(result.Buffer.End);

        await Assert.That(text.Trim()).IsEqualTo("data: {\"message\":\"hello\"}");
    }

    [Test]
    public async Task SseEndpoint_create_returns_response_with_event_stream_content_type()
    {
        WebRequestHandler handler = SseEndpoint.Create(async (sse, ct) =>
        {
            await sse.WriteJsonAsync("{\"ok\":true}", ct);
        });

        var context = CreateContext("GET", "/stream");
        var response = await handler(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(GetHeader(response, "Content-Type")).IsEqualTo("text/event-stream");
        await Assert.That(GetHeader(response, "Cache-Control")).IsEqualTo("no-cache");
    }

    private static WebContext CreateContext(string method, string path)
    {
        return WebContext.Create(
            new HttpRequest
            {
                Method = method,
                Target = path,
                Path = path,
                HeaderFields = [],
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            }
        );
    }

    private static string? GetHeader(HttpResponse response, string name)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }
        return null;
    }
}
