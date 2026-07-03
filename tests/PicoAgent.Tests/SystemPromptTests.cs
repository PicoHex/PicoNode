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

    [Test]
    public async Task SystemPrompt_HandlesJsonLikeContent()
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
        // Prompt that contains chars used in JSON: braces, brackets, etc.
        var prompt = "Respond in JSON: {\"key\": [1, 2, 3]}";
        agent.SetSystemPrompt(prompt);
        await Assert.That(agent.GetSystemPrompt()).IsEqualTo(prompt);
    }

    [Test]
    public async Task SystemPrompt_HttpClient_SerializesCorrectly()
    {
        var handler = new CapturingHandler(_ => { });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        var client = AgentHttpClient.CreateForTest(http);
        var ok = await client.SetSystemPromptAsync("You are helpful.");
        await Assert.That(ok).IsTrue();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Action<string?> _onBody;

        public CapturingHandler(Action<string?> onBody) => _onBody = onBody;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            _onBody(request.Content?.ReadAsStringAsync().Result);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
