using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class AgentRunTurnGuardTests
{
    [Test]
    public async Task RunTurn_WhenPending_Throws()
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

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "k",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );

        // Agent is Pending — RunTurn should throw
        await Assert
            .That(async () => await system.AskAsync<object?>(agent.Id, new RunTurn("hi", "t1")))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task RunTurn_WhenCompleted_Throws()
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

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "k",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);
        system.Send(agent.Id, new CompleteAgent());
        await Task.Delay(100);

        await Assert
            .That(async () => await system.AskAsync<object?>(agent.Id, new RunTurn("hi", "t1")))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task RunTurn_WhenFailed_Throws()
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

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(
                [
                    new Llm
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "k",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7()
            )
        );

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);
        system.Send(agent.Id, new FailAgent("test"));
        await Task.Delay(100);

        await Assert
            .That(async () => await system.AskAsync<object?>(agent.Id, new RunTurn("hi", "t1")))
            .Throws<DomainInvariantException>();
    }
}
