namespace PicoNode.Tests;

using System.Net;

public sealed class AgentBuilderTests
{
    [Test]
    public async Task BuildHost_WithValidConfig_ReturnsAgentHost()
    {
        var config = new AgentConfig
        {
            Providers = new()
            {
                ["test"] = new ProviderEntry
                {
                    ApiKey = "sk-test",
                    ApiFormat = "openai",
                    BaseUrl = "https://api.openai.com/v1",
                },
            },
            Model = "gpt-4",
        };

        var host = new AgentBuilder()
            .WithConfig(config)
            .BuildHost();

        await Assert.That((object?)host).IsNotNull();
    }

    [Test]
    public async Task BuildHost_WithoutConfig_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            new AgentBuilder().BuildHost();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildServer_WithValidConfig_ReturnsAgentServer()
    {
        var config = new AgentConfig
        {
            Providers = new()
            {
                ["test"] = new ProviderEntry
                {
                    ApiKey = "sk-test",
                    ApiFormat = "openai",
                    BaseUrl = "https://api.openai.com/v1",
                },
            },
            Model = "gpt-4",
        };

        await using var server = new AgentBuilder()
            .WithConfig(config)
            .BuildServer(new AgentServerOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 52525),
            });

        await Assert.That((object?)server).IsNotNull();
    }
}
