namespace PicoNode.AI.Tests.Types;

public class ModelSerializationTests
{
    [Test]
    public async Task Serialize_Model_RoundTrip()
    {
        var model = new Model
        {
            Id = "claude-sonnet-4-20250514",
            Name = "Claude Sonnet 4",
            Provider = "anthropic",
            Api = AiApiFormat.AnthropicMessages,
            BaseUrl = "https://api.anthropic.com",
            ThinkingEnabled = true,
            Cost = new ModelCost
            {
                Input = 3.0m,
                Output = 15.0m,
                CacheRead = 0.30m,
                CacheWrite = 6.0m,
            },
            ContextWindow = 200000,
            MaxTokens = 8192,
        };

        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(model);
        var restored = PicoJetson.JsonSerializer.Deserialize<Model>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Id).IsEqualTo("claude-sonnet-4-20250514");
        await Assert.That(restored.Provider).IsEqualTo("anthropic");
        await Assert.That(restored.Api).IsEqualTo(AiApiFormat.AnthropicMessages);
        await Assert.That(restored.Cost.Input).IsEqualTo(3.0m);
        await Assert.That(restored.ContextWindow).IsEqualTo(200000);
    }
}
