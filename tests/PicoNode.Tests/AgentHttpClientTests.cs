// tests/PicoNode.Tests/AgentHttpClientTests.cs
namespace PicoNode.Tests;

public sealed class AgentHttpClientTests
{
    [Test]
    public async Task Constructor_AcceptsBaseUrl()
    {
        await using var client = new AgentHttpClient("http://localhost:12345");
        await Assert.That((object?)client).IsNotNull();
    }
}
