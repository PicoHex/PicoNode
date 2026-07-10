using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class AgentSpawnChildTests
{
    [Test]
    public async Task SpawnChild_CreatesChildInRegistry()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llmClient, toolRunner)
        );

        var parent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new()
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "sk",
                    },
                ],
                "x",
                "y",
                "/tmp"
            )
        );

        var childLlms = new List<Llm>
        {
            new()
            {
                ProviderName = "a",
                ModelId = "b",
                ApiKey = "sk",
            },
        };
        system.Send(parent.Id, new SpawnChildCmd(childLlms, "a", "b", []));
        await Task.Delay(200);

        await Assert.That(parent.ChildIds.Count).IsEqualTo(1);
        var child = await system.GetAsync<DomainAgent>(parent.ChildIds[0]);
        await Assert.That(child).IsNotNull();
    }
}
