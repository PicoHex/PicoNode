using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: Server uses ActorSystem to manage Agent lifecycle.
/// </summary>
public sealed class ServerActorIntegrationTests
{
    [Test]
    public async Task Server_CreatesAgent_ThroughActorSystem()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();

        system.Register<DomainAgent>(
            createFactory: cmd => cmd switch
            {
                CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                _ => throw new InvalidOperationException(),
            },
            rebuildFactory: () => new DomainAgent(llmClient, toolRunner));

        var llms = new List<Llm> { new() { ProviderName = "x", ModelId = "y", ApiKey = "sk" } };
        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(llms, "x", "y", "/tmp"));

        // Start via command
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(100);
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);

        // RunTurn via command
        system.Send(agent.Id, new RunTurn("Hello"));
        await Task.Delay(300);
        await Assert.That(agent.Session!).IsNotNull();
    }

    [Test]
    public async Task Server_CanStopAgent()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();

        system.Register<DomainAgent>(
            createFactory: cmd => cmd switch
            {
                CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                _ => throw new InvalidOperationException(),
            },
            rebuildFactory: () => new DomainAgent(llmClient, toolRunner));

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent([new() { ProviderName = "x", ModelId = "y", ApiKey = "sk" }], "x", "y", "/tmp"));
        await system.StopAsync(agent.Id);

        // Second stop is idempotent
        await system.StopAsync(agent.Id);
    }
}
