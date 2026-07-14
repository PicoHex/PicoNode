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

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent()
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
                Guid.CreateVersion7()
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

    /// <summary>
    /// BUG: SpawnChildCmd carried a Tools field that SpawnChildAsync never read —
    /// the child was created with an empty tool list. Desired: each tool in the
    /// command is added to the spawned child.
    /// </summary>
    [Test]
    public async Task SpawnChild_PropagatesToolsToChild()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent()
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
                Guid.CreateVersion7()
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
        var childTools = new List<Tool>
        {
            new()
            {
                Name = "search",
                Description = "search tool",
                Kind = ToolKind.BuiltIn,
            },
        };

        system.Send(parent.Id, new SpawnChildCmd(childLlms, "a", "b", childTools));
        await Task.Delay(300);

        await Assert.That(parent.ChildIds.Count).IsEqualTo(1);
        var child = await system.GetAsync<DomainAgent>(parent.ChildIds[0]);
        await Assert.That(child).IsNotNull();
        await Assert.That(child!.ToolsSnapshot.Count).IsEqualTo(1);
        await Assert.That(child.ToolsSnapshot[0].Name).IsEqualTo("search");
    }
}
