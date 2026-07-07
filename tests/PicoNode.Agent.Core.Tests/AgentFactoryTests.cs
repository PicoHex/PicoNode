namespace PicoNode.Agent.Tests;

public class AgentFactoryTests
{
    [Test]
    public async Task Build_WithConfig_CreatesAgent()
    {
        var config = new AgentConfig
        {
            Providers = new Dictionary<string, ProviderEntry>
            {
                ["deepseek"] = new()
                {
                    ApiKey = "sk-test",
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiFormat = "openai",
                },
            },
            Model = "deepseek-chat",
            ThinkingEnabled = true,
            ThinkingLevel = "high",
        };

        var factory = new AgentFactory();
        var agent = factory.Build(config, "/tmp/test");

        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Pending);
        await Assert.That(agent.Llms.Count).IsEqualTo(1);
        await Assert.That(agent.CurrentLlm.ProviderName).IsEqualTo("deepseek");
        await Assert.That(agent.CurrentLlm.ModelId).IsEqualTo("deepseek-chat");
        await Assert.That(agent.CurrentLlm.ThinkingLevel).IsEqualTo(ThinkingLevel.High);
    }

    [Test]
    public async Task Build_WithBuiltInTools_RegistersThem()
    {
        var config = new AgentConfig
        {
            Providers = new Dictionary<string, ProviderEntry>
            {
                ["test"] = new() { ApiKey = "sk-xxx" },
            },
            Model = "test-model",
        };

        var factory = new AgentFactory().WithBuiltInTools();
        var agent = factory.Build(config, "/tmp/test");

        await Assert.That(agent.Tools.Count).IsGreaterThan(0);
        await Assert.That(agent.Tools.Any(t => t.Name == "read")).IsTrue();
        await Assert.That(agent.Tools.Any(t => t.Name == "bash")).IsTrue();
    }

    [Test]
    public async Task Build_WithPackages_ResolvesAndInstalls()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pico_fac_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var config = new AgentConfig
            {
                Providers = new Dictionary<string, ProviderEntry>
                {
                    ["test"] = new() { ApiKey = "sk-xxx" },
                },
                Model = "test-model",
                Packages = ["git:github.com/test/repo"],
            };

            var factory = new AgentFactory();
            var agent = factory.Build(config, tmp);

            await Assert.That(agent.Packages).IsNotNull();
            await Assert.That(agent.Packages!.Count).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task Build_NoModel_UsesFirstProviderModel()
    {
        var config = new AgentConfig
        {
            Providers = new Dictionary<string, ProviderEntry>
            {
                ["openai"] = new() { ApiKey = "sk-xxx" },
            },
            // Model not set
        };

        var factory = new AgentFactory();
        var agent = factory.Build(config, "/tmp/test");

        await Assert.That(agent.CurrentLlm.ProviderName).IsEqualTo("openai");
    }
}
