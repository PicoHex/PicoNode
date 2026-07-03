namespace PicoNode.Agent.Tests.Config;

public class ConfigLoaderTests
{
    [Test]
    public async Task SgInit_AgentConfig()
    {
        var c = new AgentConfig { Providers = new() { ["x"] = new() { ApiKey = "k" } } };
        var j = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(c);
        var r = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(j);
        await Assert.That(r!.Providers["x"].ApiKey).IsEqualTo("k");
    }

    [Test]
    public async Task Deserialize_CamelCaseJson_LoadsProviders()
    {
        var json =
            """{"providers":{"deepseek":{"apiKey":"sk-test-key","apiFormat":"openai","baseUrl":"https://api.deepseek.com/v1"}},"model":"deepseek-chat","thinkingEnabled":true,"thinkingLevel":"medium","maxTokens":4096}""";
        var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(
            Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(config!.Providers.Count).IsEqualTo(1);
        await Assert.That(config.Providers["deepseek"].ApiKey).IsEqualTo("sk-test-key");
    }

    [Test]
    public async Task Load_WithPicoCfgEnvVar_ResolvesKey()
    {
        Environment.SetEnvironmentVariable("PICO_DEEPSEEK_API_KEY", "sk-test-123");
        var json =
            """{"providers":{"test":{"apiKey":"$DEEPSEEK_API_KEY","apiFormat":"openai","baseUrl":""}}}""";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, json);
        try
        {
            var config = await ConfigLoader.LoadAsync(path);
            await Assert.That(config!.Providers["test"].ApiKey).IsEqualTo("sk-test-123");
        }
        finally
        {
            File.Delete(path);
            Environment.SetEnvironmentVariable("PICO_DEEPSEEK_API_KEY", null);
        }
    }

    [Test]
    public async Task Load_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pico-missing-{Guid.NewGuid()}.json");
        try
        {
            var config = await ConfigLoader.LoadAsync(path);
            await Assert.That(config).IsNull();
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task Validate_EmptyProviders_ReturnsError()
    {
        var result = ConfigLoader.Validate(new AgentConfig());
        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors[0]).Contains("No providers configured");
    }

    [Test]
    public async Task Validate_MissingApiKey_ReturnsError()
    {
        var config = new AgentConfig
        {
            Providers = new() { ["x"] = new ProviderEntry { ApiKey = "" } },
        };
        var result = ConfigLoader.Validate(config);
        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors[0]).Contains("apiKey");
    }

    [Test]
    public async Task Validate_ValidConfig_Passes()
    {
        var config = new AgentConfig
        {
            Providers = new() { ["x"] = new ProviderEntry { ApiKey = "sk-xxx" } },
        };
        var result = ConfigLoader.Validate(config);
        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task Load_NoPrefixEnvVar_ResolvesViaFallback()
    {
        Environment.SetEnvironmentVariable("RAW_API_KEY", "sk-raw-999");
        var json =
            """{"providers":{"test":{"apiKey":"$RAW_API_KEY","apiFormat":"openai","baseUrl":""}}}""";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, json);
        try
        {
            var config = await ConfigLoader.LoadAsync(path);
            await Assert.That(config!.Providers["test"].ApiKey).IsEqualTo("sk-raw-999");
        }
        finally
        {
            File.Delete(path);
            Environment.SetEnvironmentVariable("RAW_API_KEY", null);
        }
    }
}
