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
    public async Task CreateAsync_WithoutProviders_StartsInUnconfiguredMode()
    {
        var agent = await Agent.CreateAsync(new AgentConfig { Providers = new() });
        await Assert.That(agent).IsNotNull();
        // Should not throw — Agent starts in unconfigured/setup mode.
        await agent.DisposeAsync();
    }
}
