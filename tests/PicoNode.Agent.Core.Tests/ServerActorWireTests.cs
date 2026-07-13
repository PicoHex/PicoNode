using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: Server integration through ActorSystem.
/// Verifies Server can manage Agent lifecycle with commands.
/// </summary>
public sealed class ServerActorWireTests
{
    [Test]
    public async Task Server_CreateAndStart_ThroughActorSystem()
    {
        var (system, agent) = await CreateAgent();

        system.Send(agent.Id, new StartAgent());
        await Task.Delay(100);
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);
    }

    [Test]
    public async Task Server_SwitchLlm_ThroughCommand()
    {
        var (system, agent) = await CreateAgent(extraLlms: true);

        system.Send(agent.Id, new SwitchLlmCmd("openai", "gpt-4o"));
        await Task.Delay(100);
        await Assert.That(agent.CurrentLlm.ProviderName).IsEqualTo("openai");
        await Assert.That(agent.CurrentLlm.ModelId).IsEqualTo("gpt-4o");
    }

    [Test]
    public async Task Server_CompleteLifecycle()
    {
        var (system, agent) = await CreateAgent();

        // Start
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(50);
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);

        // RunTurn
        system.Send(agent.Id, new RunTurn("Hello", "test-turn"));
        await Task.Delay(200);
        await Assert.That(agent.Session!).IsNotNull();

        // Complete
        system.Send(agent.Id, new CompleteAgent());
        await Task.Delay(100);
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Completed);

        // Stop
        await system.StopAsync(agent.Id);
    }

    private static async Task<(ActorSystem System, DomainAgent Agent)> CreateAgent(
        bool extraLlms = false
    )
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

        var llms = new List<Llm>
        {
            new()
            {
                ProviderName = "deepseek",
                ModelId = "chat",
                ApiKey = "sk",
            },
        };
        if (extraLlms)
            llms.Add(
                new()
                {
                    ProviderName = "openai",
                    ModelId = "gpt-4o",
                    ApiKey = "sk",
                }
            );

        var agent = await system.CreateAsync<DomainAgent>(
            new CreateAgent(llms, "deepseek", "chat", Guid.CreateVersion7())
        );
        return (system, agent);
    }
}
