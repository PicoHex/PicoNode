namespace PicoNode.Tests;

using Agent = PicoAgent.Agent;

public sealed class AgentTests
{
    [Test]
    public async Task CreateAsync_ReturnsAgent()
    {
        var config = new AgentConfig
        {
            Providers = new()
            {
                ["test"] = new ProviderEntry
                {
                    ApiKey = "sk-test",
                    ApiFormat = "openai",
                    BaseUrl = "https://api.test/v1",
                },
            },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        await Assert.That((object?)agent).IsNotNull();
    }

    [Test]
    public async Task SwitchModel_AlwaysSucceeds()
    {
        var config = new AgentConfig
        {
            Providers = new()
            {
                ["test"] = new ProviderEntry
                {
                    ApiKey = "sk-test",
                    ApiFormat = "openai",
                    BaseUrl = "https://api.test/v1",
                },
            },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        var result = agent.SwitchModel("any-model-id");
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task SwitchProvider_Invalid_ReturnsFalse()
    {
        var config = new AgentConfig
        {
            Providers = new()
            {
                ["test"] = new ProviderEntry
                {
                    ApiKey = "sk-test",
                    ApiFormat = "openai",
                    BaseUrl = "https://api.test/v1",
                },
            },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        var result = agent.SwitchProvider("nonexistent");
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ListenAsync_StartsServer()
    {
        var config = new AgentConfig
        {
            Providers = new()
            {
                ["test"] = new ProviderEntry
                {
                    ApiKey = "sk-test",
                    ApiFormat = "openai",
                    BaseUrl = "https://api.test/v1",
                },
            },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        await agent.ListenAsync("http://localhost:0");
        await Assert.That(agent.LocalEndPoint).IsNotNull();
        await agent.StopAsync();
    }
}
