namespace PicoNode.Agent.Tests;

public sealed class ProcessManagerTests
{
    [Test]
    public async Task SessionProcessManager_CreateSession_CompletesAndCreatesSession()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<Domain.Agent>(cmd => cmd switch
        {
            CreateAgent c => new Domain.Agent(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<SessionActor>(cmd => cmd switch
        {
            StartSession c => new SessionActor(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<RuntimeActor>(_ => new RuntimeActor());
        system.Register<SessionProcessManager>(_ => new SessionProcessManager());

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));
        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", llm.Id));

        var pm = await system.CreateAsync<SessionProcessManager>(
            new CreateSessionCmd("My Session", agent.Id));
        // Use AskAsync instead of Send to wait for PM processing to complete
        await system.AskAsync<object?>(pm.Id, new CreateSessionCmd("My Session", agent.Id));

        // PM should have completed: verify PmRuntimeActorCreated was emitted
        var pmEvents = await store.LoadAsync(pm.Id);
        await Assert.That(pmEvents).IsNotNull();
        await Assert.That(pmEvents.Count).IsGreaterThan(0);
        var hasRuntimeCreated = pmEvents.Any(e => e is PmRuntimeActorCreated);
        await Assert.That(hasRuntimeCreated).IsTrue();
    }

    [Test]
    public async Task AgentProcessManager_CreateAgent_ValidatesLlm()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        system.Register<Domain.Agent>(cmd => cmd switch
        {
            CreateAgent c => new Domain.Agent(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<AgentProcessManager>(_ => new AgentProcessManager());

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        var pm = await system.CreateAsync<AgentProcessManager>(
            new CreateAgentCmd("MyAgent", llm.Id));
        await system.AskAsync<object?>(pm.Id, new CreateAgentCmd("MyAgent", llm.Id));

        // PM should have completed: verify PmAgentCreationCompleted event
        var pmEvents = await store.LoadAsync(pm.Id);
        await Assert.That(pmEvents).IsNotNull();
        var hasCompleted = pmEvents.Any(e => e is PmAgentCreationCompleted);
        await Assert.That(hasCompleted).IsTrue();
    }

    [Test]
    public async Task AgentProcessManager_CreateAgent_NonExistentLlm_Throws()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        system.Register<Domain.Agent>(cmd => cmd switch
        {
            CreateAgent c => new Domain.Agent(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<AgentProcessManager>(_ => new AgentProcessManager());

        var pm = await system.CreateAsync<AgentProcessManager>(
            new CreateAgentCmd("MyAgent", Guid.CreateVersion7()));
        // AskAsync will propagate the exception from the PM
        await Assert
            .That(async () => await system.AskAsync<object?>(pm.Id,
                new CreateAgentCmd("MyAgent", Guid.CreateVersion7())))
            .Throws<KeyNotFoundException>();
    }

    [Test]
    public async Task RuntimeActor_RunTurn_AppendsMessagesToSession()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<Domain.Agent>(cmd => cmd switch
        {
            CreateAgent c => new Domain.Agent(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<SessionActor>(cmd => cmd switch
        {
            StartSession c => new SessionActor(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<RuntimeActor>(_ => new RuntimeActor());

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("TestAgent", llm.Id));

        var session = await system.CreateAsync<SessionActor>(
            new StartSession("Test Session"));

        // Create and init runtime
        var runtime = await system.CreateAsync<RuntimeActor>(
            new InitRuntimeCmd(agent.Id, session.Id));
        system.Send(runtime.Id, new InitRuntimeCmd(agent.Id, session.Id));

        // Send RunTurn and wait for completion via AskAsync
        await system.AskAsync<object?>(runtime.Id, new RunTurnCmd("Hello"));

        // Verify session has both user message and assistant response
        var ctx = await system.AskAsync<SessionContext>(session.Id, new GetContextQuery());
        await Assert.That(ctx.Messages).IsNotNull();
        await Assert.That(ctx.Messages.Count).IsEqualTo(2);
        await Assert.That(ctx.Messages[0].Role).IsEqualTo(MessageRole.User);
        await Assert.That(ctx.Messages[0].Content).IsEqualTo("Hello");
        await Assert.That(ctx.Messages[1].Role).IsEqualTo(MessageRole.Assistant);
        await Assert.That(ctx.Messages[1].Content).IsEqualTo("Echo: Hello");
        await Assert.That(ctx.Messages[1].Sender).IsNotNull();
    }

    [Test]
    public async Task RuntimeActor_Init_StoresIds()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        system.Register<RuntimeActor>(_ => new RuntimeActor());

        var agentId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();

        var runtime = await system.CreateAsync<RuntimeActor>(
            new InitRuntimeCmd(agentId, sessionId));
        system.Send(runtime.Id, new InitRuntimeCmd(agentId, sessionId));

        var result = await system.AskAsync<object>(runtime.Id, new GetLoadedConfigQuery());
        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task LlmProcessManager_DeleteLlm_EmitsCompletionEvent()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        system.Register<LlmActor>(cmd => cmd switch
        {
            CreateLlm c => new LlmActor(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<LlmProcessManager>(_ => new LlmProcessManager());

        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        var pm = await system.CreateAsync<LlmProcessManager>(
            new DeleteLlmCmd(llm.Id));
        await system.AskAsync<object?>(pm.Id, new DeleteLlmCmd(llm.Id));

        // PM should have completed: verify PmLlmDeletionCompleted event
        var pmEvents = await store.LoadAsync(pm.Id);
        await Assert.That(pmEvents).IsNotNull();
        var hasCompleted = pmEvents.Any(e => e is PmLlmDeletionCompleted);
        await Assert.That(hasCompleted).IsTrue();
    }
}
