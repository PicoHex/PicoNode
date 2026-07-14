namespace PicoNode.Agent.Tests;

using DomainAgent = PicoNode.Agent.Domain.Agent;
using PicoNode.Agent.Core.Tests;

public sealed class AgentIdentityTests
{
    [Test]
    public async Task RenameAgent_PersistsName()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<DomainAgent>(cmd => cmd switch
        {
            CreateAgent c => new DomainAgent(c, new FakeLlmClient(), new FakeToolRunner()),
            _ => throw new InvalidOperationException(),
        });

        var agent = await system.CreateAsync<DomainAgent>(new CreateAgent(
            [new Llm { ProviderName = "test-provider", ModelId = "test-model", ApiKey = "sk-test" }],
            "test-provider", "test-model",
            ParentId: null, Packages: null, Name: "MyAgent"));

        system.Send(agent.Id, new RenameAgent("Programmer"));
        await system.AskAsync<string>(agent.Id, new GetAgentNameQuery()); // force ordering

        var name = await system.AskAsync<string>(agent.Id, new GetAgentNameQuery());
        await Assert.That(name).IsEqualTo("Programmer");
    }

    [Test]
    public async Task GetConfigQuery_ReturnsSnapshot()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<DomainAgent>(cmd => cmd switch
        {
            CreateAgent c => new DomainAgent(c, new FakeLlmClient(), new FakeToolRunner()),
            _ => throw new InvalidOperationException(),
        });

        var agent = await system.CreateAsync<DomainAgent>(new CreateAgent(
            [new Llm { ProviderName = "test-provider", ModelId = "test-model", ApiKey = "sk-test" }],
            "test-provider", "test-model",
            ParentId: null, Packages: null, Name: "ProgrammerAI"));

        var snap = await system.AskAsync<AgentConfigSnapshot>(agent.Id, new GetConfigQuery());
        await Assert.That(snap.Name).IsEqualTo("ProgrammerAI");
        await Assert.That(snap.Llms).IsNotEmpty();
    }
}
