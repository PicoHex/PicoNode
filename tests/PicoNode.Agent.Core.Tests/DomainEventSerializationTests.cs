using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class DomainEventSerializationTests
{
    private static readonly Llm SampleLlm = new()
    {
        ProviderName = "test-provider",
        ModelId = "test-model",
        ApiKey = "sk-test",
        BaseUrl = "https://test.example.com/v1",
        ThinkingEnabled = true,
        MaxTokens = 4096,
    };

    private static readonly Tool SampleTool = new()
    {
        Name = "test-tool",
        Description = "A test tool",
    };

    [Test]
    public async Task AgentCreated_RoundTrip()
    {
        var e = new AgentCreated([SampleLlm], "test-provider", "test-model", Guid.CreateVersion7(), null, ["pkg1"]);
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);

        await Assert.That(result).IsTypeOf<AgentCreated>();
        var ac = (AgentCreated)result;
        await Assert.That(ac.Llms.Count).IsEqualTo(1);
        await Assert.That(ac.Llms[0].ProviderName).IsEqualTo("test-provider");
        await Assert.That(ac.CurrentProvider).IsEqualTo("test-provider");
        await Assert.That(ac.CurrentModel).IsEqualTo("test-model");
        await Assert.That(ac.Packages).IsEquivalentTo(["pkg1"]);
    }

    [Test]
    public async Task AgentStarted_RoundTrip()
    {
        var e = new AgentStarted();
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<AgentStarted>();
    }

    [Test]
    public async Task AgentCompleted_RoundTrip()
    {
        var e = new AgentCompleted();
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<AgentCompleted>();
    }

    [Test]
    public async Task AgentFailed_RoundTrip()
    {
        var e = new AgentFailed("test reason");
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<AgentFailed>();
        await Assert.That(((AgentFailed)result).Reason).IsEqualTo("test reason");
    }

    [Test]
    public async Task LlmSwitched_RoundTrip()
    {
        var e = new LlmSwitched("p", "m");
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<LlmSwitched>();
        await Assert.That(((LlmSwitched)result).ProviderName).IsEqualTo("p");
        await Assert.That(((LlmSwitched)result).ModelId).IsEqualTo("m");
    }

    [Test]
    public async Task LlmAdded_RoundTrip()
    {
        var e = new LlmAdded(SampleLlm);
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<LlmAdded>();
        await Assert.That(((LlmAdded)result).Llm.ProviderName).IsEqualTo("test-provider");
    }

    [Test]
    public async Task LlmRemoved_RoundTrip()
    {
        var e = new LlmRemoved("p", "m");
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<LlmRemoved>();
    }

    [Test]
    public async Task ToolAdded_RoundTrip()
    {
        var e = new ToolAdded(SampleTool);
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<ToolAdded>();
        await Assert.That(((ToolAdded)result).Tool.Name).IsEqualTo("test-tool");
    }

    [Test]
    public async Task ToolRemoved_RoundTrip()
    {
        var e = new ToolRemoved("test-tool");
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<ToolRemoved>();
    }

    [Test]
    public async Task ChildSpawned_RoundTrip()
    {
        var id = Guid.CreateVersion7();
        var e = new ChildSpawned(id);
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<ChildSpawned>();
        await Assert.That(((ChildSpawned)result).ChildId).IsEqualTo(id);
    }

    [Test]
    public async Task ThinkingLevelSet_RoundTrip()
    {
        var e = new ThinkingLevelSet("xhigh");
        var json = DomainEventSerializer.Serialize(e);
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = DomainEventSerializer.Deserialize(bytes);
        await Assert.That(result).IsTypeOf<ThinkingLevelSet>();
        await Assert.That(((ThinkingLevelSet)result).Level).IsEqualTo("xhigh");
    }
}
