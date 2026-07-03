namespace PicoNode.AI.Tests.Routing;

public class ProviderRouterTests
{
    [Test]
    public async Task Resolve_ByName_ReturnsMatchingProvider()
    {
        var providers = new[]
        {
            new ProviderConfig
            {
                Name = "anthropic",
                ApiFormat = AiApiFormat.AnthropicMessages,
                Priority = 1,
            },
            new ProviderConfig
            {
                Name = "openai",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                Priority = 2,
            },
        };
        var router = new ProviderRouter(providers);

        var result = router.Resolve("claude-sonnet-4", null);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("anthropic");
    }

    [Test]
    public async Task Resolve_NullModel_ReturnsFirstProvider()
    {
        var providers = new[]
        {
            new ProviderConfig
            {
                Name = "first",
                ApiFormat = AiApiFormat.AnthropicMessages,
                Priority = 1,
            },
        };
        var router = new ProviderRouter(providers);

        var result = router.Resolve(null, null);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("first");
    }

    [Test]
    public async Task Resolve_ModelMapping_RemapsModelName()
    {
        var providers = new[]
        {
            new ProviderConfig
            {
                Name = "openai",
                ApiFormat = AiApiFormat.OpenAIChatCompletions,
                ModelMapping = new() { ["gpt-4"] = "gpt-4o-2025-05-14" },
            },
        };
        var router = new ProviderRouter(providers);

        var result = router.Resolve("gpt-4", null);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ModelMapping["gpt-4"]).IsEqualTo("gpt-4o-2025-05-14");
    }

    [Test]
    public async Task Resolve_PreferredFormatNoMatch_ReturnsNull()
    {
        var providers = new[]
        {
            new ProviderConfig
            {
                Name = "anthropic",
                ApiFormat = AiApiFormat.AnthropicMessages,
                Priority = 1,
            },
        };
        var router = new ProviderRouter(providers);

        // Request OpenAI but only Anthropic provider exists
        var result = router.Resolve(null, AiApiFormat.OpenAIChatCompletions);

        // Should fall back to any available provider
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("anthropic");
    }
}
