namespace PicoNode.Tests;

using Agent = PicoAgent.Agent;

public sealed class AgentBuilderTests
{
    [Test]
    public async Task CreateAsync_WithValidConfig_ReturnsAgent()
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

        await using var agent = await Agent.CreateAsync(config);
        await Assert.That((object?)agent).IsNotNull();
    }

    [Test]
    public async Task CreateAsync_WithoutConfig_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await Agent.CreateAsync(new AgentConfig { Providers = new() });
        });
    }
}
