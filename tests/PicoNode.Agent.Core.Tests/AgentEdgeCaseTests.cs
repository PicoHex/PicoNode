using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class AgentEdgeCaseTests
{
    /// <summary>
    /// Unknown commands must throw, not be silently ignored.
    /// </summary>
    [Test]
    public async Task UnknownCommand_Throws()
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
            () => new DomainAgent(new FakeLlmClient(), new FakeToolRunner())
        );

        var agent = await system.CreateAsync<DomainAgent>(
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

        try
        {
            await system.AskAsync<object?>(agent.Id, new UnknownTestCommand());
            await Assert.That(false).IsTrue(); // Should not reach here
        }
        catch (Exception ex)
        {
            await Assert.That(ex is DomainInvariantException).IsTrue();
        }
    }

    /// <summary>
    /// RunTurn without infrastructure must throw clearly, not NPE.
    /// </summary>
    [Test]
    public async Task RunTurn_WithoutInfrastructure_Faults()
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
            rebuildFactory: null
        );

        var agent = await system.CreateAsync<DomainAgent>(
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

        await Assert
            .That(async () => await system.AskAsync<object?>(agent.Id, new RunTurn("Hi")))
            .Throws<Exception>();
    }
}

internal sealed record UnknownTestCommand : ICommand;
