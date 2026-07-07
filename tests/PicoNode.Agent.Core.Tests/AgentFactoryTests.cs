namespace PicoNode.Agent.Tests;

public class AgentFactoryTests
{
    [Test]
    public async Task Build_WithConfig_CreatesAgent()
    {
        var config = new Domain.AgentConfig
        {
            Providers = new Dictionary<string, Domain.ProviderEntry>
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
    public async Task Build_NoModel_Throws()
    {
        var config = new Domain.AgentConfig
        {
            Providers = new Dictionary<string, Domain.ProviderEntry>
            {
                ["openai"] = new() { ApiKey = "sk-xxx" },
            },
            // Model not set
        };

        var factory = new AgentFactory();
        await Assert
            .That(() => factory.Build(config, "/tmp/test"))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task Build_WithBuiltInTools_RegistersThem()
    {
        var config = new Domain.AgentConfig
        {
            Providers = new Dictionary<string, Domain.ProviderEntry>
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
        await Assert.That(agent.Tools.Any(t => t.Name == "edit")).IsTrue();
        await Assert.That(agent.Tools.Any(t => t.Name == "grep")).IsTrue();
    }

    [Test]
    public async Task Build_WithBuiltInTools_ToolRunnerCanExecute()
    {
        var config = new Domain.AgentConfig
        {
            Providers = new Dictionary<string, Domain.ProviderEntry>
            {
                ["test"] = new() { ApiKey = "sk-xxx" },
            },
            Model = "test-model",
        };

        var factory = new AgentFactory().WithBuiltInTools();
        factory.Build(config, "/tmp/test");
        var runner = factory.GetToolRunner();

        var result = await runner.ExecuteAsync(
            "read",
            new Dictionary<string, object?> { ["path"] = "/nonexistent" },
            CancellationToken.None
        );
        await Assert.That(result).Contains("File not found");
    }

    [Test]
    public async Task Build_WithPackages_ResolvesAndInstalls()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pico_fac_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var config = new Domain.AgentConfig
            {
                Providers = new Dictionary<string, Domain.ProviderEntry>
                {
                    ["test"] = new() { ApiKey = "sk-xxx" },
                },
                Model = "test-model",
                Packages = ["git:github.com/test/repo"],
            };

            var factory = new AgentFactory();
            var agent = factory.Build(config, tmp);

            await Assert.That(agent.Packages).IsNotNull();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
