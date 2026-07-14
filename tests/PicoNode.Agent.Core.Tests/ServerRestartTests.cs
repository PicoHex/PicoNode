using PicoNode.Actor;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class ServerRestartTests
{
    [Test]
    public async Task StartAgent_OnAlreadyRunningAgent_Throws()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var sessionId = Guid.CreateVersion7();
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent()
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
                sessionId
            )
        );

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);

        // Second StartAgent on Running agent should throw
        await Assert
            .That(async () => await system.AskAsync<object?>(agent.Id, new StartAgent()))
            .Throws<DomainInvariantException>();
    }
}
