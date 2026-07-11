using PicoJetson;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: verify providers endpoint returns valid JSON array.
/// </summary>
public sealed class ConfigProvidersTests
{
    [Test]
    public async Task DefaultProviderTemplates_SerializesAsJsonArray()
    {
        var json = JsonSerializer.Serialize(
            new List<ProviderTemplate>
            {
                new()
                {
                    Name = "openai",
                    Label = "OpenAI",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiFormat = "openai",
                },
                new()
                {
                    Name = "anthropic",
                    Label = "Anthropic",
                    BaseUrl = "https://api.anthropic.com",
                    ApiFormat = "anthropic",
                },
                new()
                {
                    Name = "deepseek",
                    Label = "DeepSeek",
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiFormat = "openai",
                },
                new()
                {
                    Name = "groq",
                    Label = "Groq",
                    BaseUrl = "https://api.groq.com/openai/v1",
                    ApiFormat = "openai",
                },
                new()
                {
                    Name = "ollama",
                    Label = "Ollama",
                    BaseUrl = "http://localhost:11434/v1",
                    ApiFormat = "openai",
                },
            }
        );

        await Assert.That(json.TrimStart()).StartsWith("[");
        await Assert.That(json).Contains("\"openai\"");
        await Assert.That(json).Contains("\"anthropic\"");
        await Assert.That(json).Contains("\"deepseek\"");
        await Assert
            .That(
                json.Contains("OpenAI") || json.Contains("Anthropic") || json.Contains("DeepSeek")
            )
            .IsTrue();
    }
}
