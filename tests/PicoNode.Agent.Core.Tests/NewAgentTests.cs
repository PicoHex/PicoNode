using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>Fake ILlmClient for unit tests.</summary>
internal sealed class FakeLlmClient : ILlmClient
{
    public Task<Message> CompleteAsync(Llm llm, List<Message> context,
        IReadOnlyList<Tool> tools, CancellationToken ct) => Task.FromResult(new Message());
}
internal sealed class FakeToolRunner : IToolRunner
{
    public Task<string> ExecuteAsync(string n, Dictionary<string, object?> a, CancellationToken ct)
        => Task.FromResult("ok");
}

public sealed class AgentLifecycleTests
{
    [Test]
    public async Task Create_And_Start_TransitionsToRunning()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llms = new List<Llm> { new() { ProviderName = "deepseek", ModelId = "chat", ApiKey = "sk" } };

        system.Register<DomainAgent>(
            cmd => cmd switch { CreateAgent c => new DomainAgent(c), _ => throw new InvalidOperationException() },
            () => new DomainAgent(new FakeLlmClient(), new FakeToolRunner()));

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(llms, "deepseek", "chat", "/tmp"));
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(100);
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);
    }
}
