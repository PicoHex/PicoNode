using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class ConfigLoaderTests
{
    [Test]
    public async Task LoadFromCfg_BindsAgentConfig()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "picocfg-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "providers": {
                "deepseek": {
                  "apiKey": "sk-test-key-123",
                  "baseUrl": "https://api.deepseek.com/v1",
                  "apiFormat": "openai"
                }
              },
              "model": "deepseek-v4-flash",
              "thinkingEnabled": true,
              "thinkingLevel": "xhigh",
              "maxTokens": 393216
            }
            """
        );

        try
        {
            var cfg = await Cfg.CreateBuilder().AddJsonFile(path).BuildAsync();
            var config = ConfigLoader.Load(cfg);

            await Assert.That(config).IsNotNull();
            await Assert.That(config.Providers.Count).IsEqualTo(1);
            await Assert.That(config.Providers["deepseek"].ApiKey).IsEqualTo("sk-test-key-123");
            await Assert
                .That(config.Providers["deepseek"].BaseUrl)
                .IsEqualTo("https://api.deepseek.com/v1");
            await Assert.That(config.Model).IsEqualTo("deepseek-v4-flash");
            await Assert.That(config.ThinkingEnabled).IsTrue();
            await Assert.That(config.ThinkingLevel).IsEqualTo("xhigh");
            await Assert.That(config.MaxTokens).IsEqualTo(393216);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadFromCfg_EmptyProviders_ReturnsEmptyConfig()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "picocfg-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(path, """{"providers":{},"model":"unconfigured"}""");

        try
        {
            var cfg = await Cfg.CreateBuilder().AddJsonFile(path).BuildAsync();
            var config = ConfigLoader.Load(cfg);

            await Assert.That(config).IsNotNull();
            await Assert.That(config.Providers.Count).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LoadFromCfg_CustomProvider_NotInHardcodedList()
    {
        // This provider name is NOT in the hardcoded ProviderNames list.
        // With GetAll() dynamic discovery, it should still be found.
        var dir = Path.Combine(
            Path.GetTempPath(),
            "picocfg-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "providers": {
                "custom-llm-api": {
                  "apiKey": "sk-custom-999",
                  "baseUrl": "https://custom.api.com/v1"
                }
              }
            }
            """
        );

        try
        {
            var cfg = await Cfg.CreateBuilder().AddJsonFile(path).BuildAsync();
            var config = ConfigLoader.Load(cfg);

            await Assert.That(config).IsNotNull();
            await Assert.That(config.Providers.Count).IsEqualTo(1);
            await Assert.That(config.Providers["custom-llm-api"].ApiKey).IsEqualTo("sk-custom-999");
            await Assert
                .That(config.Providers["custom-llm-api"].BaseUrl)
                .IsEqualTo("https://custom.api.com/v1");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
