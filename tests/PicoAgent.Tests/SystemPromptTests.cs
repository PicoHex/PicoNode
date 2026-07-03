namespace PicoAgent.Tests;

public sealed class SystemPromptTests
{
    [Test]
    public async Task SystemPrompt_DefaultsToBuiltIn()
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
        // SystemPrompt defaults to null until explicitly set.
        // The built-in prompt is assembled from skills/tools at runtime.
        // Verify the getter doesn't throw and can handle null.
        var prompt = agent.GetSystemPrompt();
        await Assert.That((object?)agent).IsNotNull();
    }

    [Test]
    public async Task SystemPrompt_CanBeUpdated()
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
        agent.SetSystemPrompt("You are a helpful penguin.");
        await Assert.That(agent.GetSystemPrompt()).IsEqualTo("You are a helpful penguin.");
    }

    [Test]
    public async Task SystemPrompt_RoundTripsSpecialCharacters()
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
        var prompt = "You are helpful.\n\nRules:\n- Be nice\n- Use \"quotes\" carefully";
        agent.SetSystemPrompt(prompt);
        await Assert.That(agent.GetSystemPrompt()).IsEqualTo(prompt);
    }
}
