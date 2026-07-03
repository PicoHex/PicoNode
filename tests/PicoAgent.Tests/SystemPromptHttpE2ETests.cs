namespace PicoAgent.Tests;

public sealed class SystemPromptHttpE2ETests
{
    [Test]
    public async Task SetSystemPrompt_HttpRoundTrip_IsStored()
    {
        var config = new AgentConfig
        {
            Providers = new() { ["test"] = new ProviderEntry { ApiKey = "sk-test", ApiFormat = "openai", BaseUrl = "https://api.openai.com/v1" } },
            Model = "gpt-4",
        };
        await using var agent = await Agent.CreateAsync(config);
        await agent.ListenAsync("http://localhost:19999");

        try
        {
            // Use AgentHttpClient to go through full HTTP chain
            var client = new AgentHttpClient("http://localhost:19999");

            // Set system prompt via HTTP
            var ok = await client.SetSystemPromptAsync("你是一个作家，请用文言文回答。");
            await Assert.That(ok).IsTrue();

            // Verify stored in-memory via AgentHost
            var host = agent.GetHostForTesting();
            await Assert.That(host.GetSystemPrompt()).Contains("你是一个作家");
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
        }
    }
}
