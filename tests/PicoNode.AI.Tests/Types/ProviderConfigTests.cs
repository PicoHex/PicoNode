namespace PicoNode.AI.Tests.Types;
using PicoNode.AI;


public class ProviderConfigTests
{
    [Test]
    public async Task Serialize_ProviderConfig_RoundTrip()
    {
        var config = new ProviderConfig
        {
            Name = "anthropic",
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "sk-ant-xxx",
            ApiFormat = AiApiFormat.AnthropicMessages,
            ModelMapping = new() { ["claude-sonnet"] = "claude-sonnet-4-20250514" },
            Priority = 1,
        };

        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(config);
        var restored = PicoJetson.JsonSerializer.Deserialize<ProviderConfig>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Name).IsEqualTo("anthropic");
        await Assert.That(restored.Priority).IsEqualTo(1);
        await Assert.That(restored.ModelMapping["claude-sonnet"])
            .IsEqualTo("claude-sonnet-4-20250514");
    }

    [Test]
    public async Task Serialize_ProviderHealth_RoundTrip()
    {
        var health = new ProviderHealth
        {
            State = CircuitState.Open,
            ConsecutiveFailures = 5,
            ErrorRate = 0.8,
            LastFailure = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(health);
        var restored = PicoJetson.JsonSerializer.Deserialize<ProviderHealth>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.State).IsEqualTo(CircuitState.Open);
        await Assert.That(restored.ConsecutiveFailures).IsEqualTo(5);
    }
}
