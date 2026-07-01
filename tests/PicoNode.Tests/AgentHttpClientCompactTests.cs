namespace PicoNode.Tests;

public class AgentHttpClientCompactTests
{
    [Test]
    public async Task CompactSessionAsync_DefaultKeepRecent_SendsEmptyBody()
    {
        var seenBody = "";
        var handler = new TestDelegatingHandler(async (req, ct) =>
        {
            seenBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\"compressedCount\":0}") };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = global::PicoAgent.AgentHttpClient.CreateForTest(http);

        await client.CompactSessionAsync("s1");
        await Assert.That(seenBody).IsEqualTo("{}");
    }
}

public sealed class TestDelegatingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public TestDelegatingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) => _handler(request, ct);
}
