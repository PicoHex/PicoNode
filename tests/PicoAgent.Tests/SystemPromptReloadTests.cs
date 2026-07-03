namespace PicoAgent.Tests;

public sealed class SystemPromptReloadTests
{
    [Test]
    public async Task ReplaceLoop_PreservesSystemPrompt()
    {
        // Simulate: set prompt → config reload → prompt should survive
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
        var host = agent.GetHostForTesting();

        // Set custom prompt
        host.SetSystemPrompt("You are a penguin.");
        await Assert.That(host.GetSystemPrompt()).IsEqualTo("You are a penguin.");

        // Simulate config reload: replace the loop
        var newLoop = new AgentLoop(
            new NoopAgentLlm(),
            new CapabilityRegistry(),
            new CapabilityRunner()
        );
        host.ReplaceLoop(newLoop);

        // System prompt should survive the reload
        await Assert.That(host.GetSystemPrompt()).IsEqualTo("You are a penguin.");
    }
}
