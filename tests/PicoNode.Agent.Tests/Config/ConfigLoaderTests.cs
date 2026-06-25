namespace PicoNode.Agent.Tests.Config;

using PicoNode.Agent;

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
    public async Task Load_WithEnvVarExpansion_ResolvesKey()
    {
        Environment.SetEnvironmentVariable("MY_KEY", "sk-test-123");
        var json = """{"providers":{"test":{"apiKey":"$MY_KEY"}}}""";
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, json);
        try
        {
            var config = ConfigLoader.Load(path);
            await Assert.That(config.Providers["test"].ApiKey).IsEqualTo("sk-test-123");
        }
        finally { File.Delete(path); Environment.SetEnvironmentVariable("MY_KEY", null); }
    }

    [Test]
    public async Task Load_MissingFile_CreatesTemplate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pico-config-{Guid.NewGuid()}.json");
        try
        {
            var config = ConfigLoader.Load(path);
            await Assert.That(config).IsNull();
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
