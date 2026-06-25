namespace PicoNode.AI.Tests;

using PicoNode.AI;
using PicoNode.AI;

public class ModelDiscoveryTests
{
    [Test]
    public async Task Discover_FromDeepSeek_ReturnsModels()
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
        if (string.IsNullOrEmpty(apiKey))
            return;

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var provider = new ProviderConfig
        {
            Name = "deepseek",
            BaseUrl = "https://api.deepseek.com/v1",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
            ApiKey = apiKey,
        };

        var models = await ModelDiscovery.DiscoverAsync(http, provider, CancellationToken.None);

        await Assert.That(models.Length).IsGreaterThan(0);
        await Assert.That(models[0].Id).IsTypeOf<string>();
        Console.WriteLine($"Models: {string.Join(", ", models.Select(m => m.Id))}");

        // v4 models
        var hasV4 = models.Any(m => m.Id.Contains("v4"));
        await Assert.That(hasV4).IsTrue();
    }

    [Test]
    public async Task Discover_NoApiKey_ReturnsEmpty()
    {
        var http = new HttpClient();
        var provider = new ProviderConfig
        {
            Name = "test",
            BaseUrl = "https://api.deepseek.com/v1",
            ApiFormat = AiApiFormat.OpenAIChatCompletions,
            ApiKey = "invalid",
        };

        var models = await ModelDiscovery.DiscoverAsync(http, provider, CancellationToken.None);
        await Assert.That(models.Length).IsEqualTo(0);
    }
}
