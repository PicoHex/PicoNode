namespace PicoNode.AI.Tests.Types;

using PicoNode.AI;

public class ProviderPresetsTests
{
    [Test]
    public async Task GetPreset_Anthropic_ReturnsConfig()
    {
        var config = ProviderPresets.Get("anthropic");
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.ApiFormat).IsEqualTo(AiApiFormat.AnthropicMessages);
        await Assert.That(config.BaseUrl).IsEqualTo("https://api.anthropic.com");
    }

    [Test]
    public async Task GetPreset_OpenAI_ReturnsConfig()
    {
        var config = ProviderPresets.Get("openai");
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.ApiFormat).IsEqualTo(AiApiFormat.OpenAIChatCompletions);
        await Assert.That(config.BaseUrl).IsEqualTo("https://api.openai.com/v1");
    }

    [Test]
    public async Task GetPreset_DeepSeek_ReturnsConfig()
    {
        var config = ProviderPresets.Get("deepseek");
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.ApiFormat).IsEqualTo(AiApiFormat.OpenAIChatCompletions);
        await Assert.That(config.BaseUrl).IsEqualTo("https://api.deepseek.com/v1");
    }

    [Test]
    public async Task GetPreset_Unknown_ReturnsNull()
    {
        var config = ProviderPresets.Get("nonexistent");
        await Assert.That(config).IsNull();
    }

    [Test]
    public async Task All_ContainsSevenPresets()
    {
        var all = ProviderPresets.All;
        await Assert.That(all.Count).IsEqualTo(7);
    }
}
