namespace PicoNode.Agent.Tests;

using DomainAgent = PicoNode.Agent.Domain.Agent;
using PicoNode.Agent.Core.Tests;

public sealed class RuntimeActorTests
{
    [Test]
    public async Task LoadAgent_PullsConfigFromAgentActor()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<DomainAgent>(cmd => cmd switch
        {
            CreateAgent c => new DomainAgent(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<RuntimeActor>(cmd => cmd switch
        {
            NoOpCmd => new RuntimeActor(),
            _ => throw new InvalidOperationException(),
        });

        var agent = await system.CreateAsync<DomainAgent>(new CreateAgent(
            [new Llm { ProviderName = "p", ModelId = "m", ApiKey = "sk-test" }],
            "p", "m", ParentId: null, Packages: null, Name: "Prog"));
        var runtime = await system.CreateAsync<RuntimeActor>(new NoOpCmd());

        system.Send(runtime.Id, new LoadAgentCmd(agent.Id));
        var loaded = await system.AskAsync<AgentConfigSnapshot>(
            runtime.Id, new GetLoadedConfigQuery());

        await Assert.That(loaded.Name).IsEqualTo("Prog");
    }
}
