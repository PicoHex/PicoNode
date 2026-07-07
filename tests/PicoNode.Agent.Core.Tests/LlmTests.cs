namespace PicoNode.Agent.Tests;

using PicoNode.Agent.Domain;

public class LlmTests
{
    [Test]
    public async Task Equality_SameProviderAndModel_AreEqual()
    {
        var a = new Llm
        {
            ProviderName = "anthropic",
            ModelId = "claude-sonnet-4",
            ApiKey = "sk-a",
            BaseUrl = "https://api.anthropic.com",
            ApiFormat = AiApiFormat.AnthropicMessages,
            ThinkingLevel = ThinkingLevel.XHigh,
            MaxTokens = 4096,
            ThinkingEnabled = true,
        };
        var b = new Llm
        {
            ProviderName = "anthropic",
            ModelId = "claude-sonnet-4",
            ApiKey = "sk-b",
            BaseUrl = "https://api.anthropic.com",
            ApiFormat = AiApiFormat.AnthropicMessages,
            ThinkingLevel = ThinkingLevel.XHigh,
            MaxTokens = 4096,
            ThinkingEnabled = true,
        };
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Equality_DifferentProvider_AreNotEqual()
    {
        var a = new Llm { ProviderName = "anthropic", ModelId = "claude-sonnet-4" };
        var b = new Llm { ProviderName = "openai", ModelId = "claude-sonnet-4" };
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task Equality_DifferentModel_AreNotEqual()
    {
        var a = new Llm { ProviderName = "anthropic", ModelId = "sonnet" };
        var b = new Llm { ProviderName = "anthropic", ModelId = "opus" };
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task GetHashCode_SameIdentity_SameHash()
    {
        var a = new Llm
        {
            ProviderName = "a",
            ModelId = "b",
            ApiKey = "x",
        };
        var b = new Llm
        {
            ProviderName = "a",
            ModelId = "b",
            ApiKey = "y",
        };
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
