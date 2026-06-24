namespace PicoNode.AI.Tests.LLm;

using PicoNode.AI;

public sealed class MockHttpHandler : HttpMessageHandler
{
    public HttpResponseMessage? NextResponse { get; set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(NextResponse
            ?? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(""),
            });
    }
}
