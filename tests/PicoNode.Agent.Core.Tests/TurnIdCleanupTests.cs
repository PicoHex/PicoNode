using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class TurnIdCleanupTests
{
    [Test]
    public async Task RunTurn_ClearsTurnId_AfterCompletion()
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
                        ProviderName = "d",
                        ModelId = "m",
                        ApiKey = "k",
                    },
                ],
                "d",
                "m"
            )
        );
        await Task.Delay(50);

        system.Send(agent.Id, new RunTurn("hello", "turn-abc"));
        await Task.Delay(500);

        await Assert.That(agent.TurnId).IsNull();
    }
}
