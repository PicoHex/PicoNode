namespace PicoNode.Agent.Tests;

public sealed class AgentIdentityTests
{
    private static IActorSystem BuildSystem()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<Domain.Agent>(cmd => cmd switch
        {
            CreateAgent c => new Domain.Agent(c),
            _ => throw new InvalidOperationException(),
        });
        return system;
    }

    [Test]
    public async Task CreateAgent_WithLlmId_ReadsBack()
    {
        var system = BuildSystem();
        var llmId = Guid.CreateVersion7();
        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", llmId));

        var snap = await system.AskAsync<AgentConfigSnapshot>(agent.Id, new GetConfigQuery());
        await Assert.That(snap.Name).IsEqualTo("MyAgent");
        await Assert.That(snap.LlmId).IsEqualTo(llmId);
    }

    [Test]
    public async Task CreateAgent_EmptyName_Throws()
    {
        var system = BuildSystem();
        await Assert
            .That(async () => await system.CreateAsync<Domain.Agent>(
                new CreateAgent("", Guid.CreateVersion7())))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task CreateAgent_EmptyLlmId_Throws()
    {
        var system = BuildSystem();
        await Assert
            .That(async () => await system.CreateAsync<Domain.Agent>(
                new CreateAgent("MyAgent", Guid.Empty)))
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task SetLlm_ChangesLlmId()
    {
        var system = BuildSystem();
        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", Guid.CreateVersion7()));

        var newLlmId = Guid.CreateVersion7();
        system.Send(agent.Id, new SetLlmCmd(newLlmId));
        var snap = await system.AskAsync<AgentConfigSnapshot>(agent.Id, new GetConfigQuery());
        await Assert.That(snap.LlmId).IsEqualTo(newLlmId);
    }

    [Test]
    public async Task SetMaxTokens_ReadsBack()
    {
        var system = BuildSystem();
        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", Guid.CreateVersion7()));

        system.Send(agent.Id, new SetMaxTokensCmd(8192));
        var snap = await system.AskAsync<AgentConfigSnapshot>(agent.Id, new GetConfigQuery());
        await Assert.That(snap.MaxTokens).IsEqualTo(8192);
    }

    [Test]
    public async Task SetThinkingEnabled_ReadsBack()
    {
        var system = BuildSystem();
        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", Guid.CreateVersion7()));

        system.Send(agent.Id, new SetThinkingEnabledCmd(false));
        var snap = await system.AskAsync<AgentConfigSnapshot>(agent.Id, new GetConfigQuery());
        await Assert.That(snap.ThinkingEnabled).IsFalse();
    }

    [Test]
    public async Task AddTool_ReadsBack()
    {
        var system = BuildSystem();
        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", Guid.CreateVersion7()));

        system.Send(agent.Id, new AddToolCmd(new Tool
        {
            Name = "my_tool",
            Description = "A test tool",
            Kind = ToolKind.BuiltIn
        }));
        var snap = await system.AskAsync<AgentConfigSnapshot>(agent.Id, new GetConfigQuery());
        await Assert.That(snap.Tools).Count().IsEqualTo(1);
        await Assert.That(snap.Tools[0].Name).IsEqualTo("my_tool");
    }

    [Test]
    public async Task RenameAgent_ReadsBack()
    {
        var system = BuildSystem();
        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", Guid.CreateVersion7()));

        system.Send(agent.Id, new RenameAgent("RenamedAgent"));
        var name = await system.AskAsync<string>(agent.Id, new GetAgentNameQuery());
        await Assert.That(name).IsEqualTo("RenamedAgent");
    }

    [Test]
    public async Task DeleteAgent_StillQueryable()
    {
        var system = BuildSystem();
        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", Guid.CreateVersion7()));

        system.Send(agent.Id, new DeleteAgent());
        var name = await system.AskAsync<string>(agent.Id, new GetAgentNameQuery());
        await Assert.That(name).IsEqualTo("MyAgent");
    }

    [Test]
    public async Task EventsPersistInStore()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<Domain.Agent>(cmd => cmd switch
        {
            CreateAgent c => new Domain.Agent(c),
            _ => throw new InvalidOperationException(),
        });

        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", Guid.CreateVersion7()));

        system.Send(agent.Id, new SetMaxTokensCmd(16384));

        // Register rebuild factory
        system.Register<Domain.Agent>(
            _ => throw new InvalidOperationException(),
            () => new Domain.Agent());

        var rebuilt = await system.GetAsync<Domain.Agent>(agent.Id);
        await Assert.That(rebuilt).IsNotNull();

        var snap = await system.AskAsync<AgentConfigSnapshot>(rebuilt!.Id, new GetConfigQuery());
        await Assert.That(snap.Name).IsEqualTo("MyAgent");
        await Assert.That(snap.MaxTokens).IsEqualTo(16384);
    }
}
