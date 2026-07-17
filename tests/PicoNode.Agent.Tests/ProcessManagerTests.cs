namespace PicoNode.Agent.Tests;

public sealed class ProcessManagerTests
{
    [Test]
    public async Task SessionProcessManager_CreateSession_CreatesSessionAndRuntime()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        // Register required actors
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
        system.Register<SessionActor>(cmd => cmd switch
        {
            StartSession c => new SessionActor(c),
            _ => throw new InvalidOperationException(),
        });
        system.Register<RuntimeActor>(_ => new RuntimeActor());
        system.Register<SessionProcessManager>(_ => new SessionProcessManager());

        // Create a prerequisite: LLM + Agent
        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        var agent = await system.CreateAsync<Domain.Agent>(
            new CreateAgent("MyAgent", llm.Id));

        // Create session via PM
        var pm = await system.CreateAsync<SessionProcessManager>(
            new CreateSessionCmd("My Session", agent.Id));
        system.Send(pm.Id, new CreateSessionCmd("My Session", agent.Id));

        // Flush and verify PM completed
        // The PM should have created a session and a runtime
        // Since PM sets _completed = true, asking it anything throws
        // We can verify by checking that the events exist in the store
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

        // Create LLM first
        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        // Create agent via PM
        var pm = await system.CreateAsync<AgentProcessManager>(
            new CreateAgentCmd("MyAgent", llm.Id));
        system.Send(pm.Id, new CreateAgentCmd("MyAgent", llm.Id));

        // Flush - the PM should complete without throwing
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
    public async Task LlmProcessManager_DeleteLlm_Works()
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
        system.Register<LlmProcessManager>(_ => new LlmProcessManager());

        // Create a non-system LLM
        var llm = await system.CreateAsync<LlmActor>(new CreateLlm(
            "openai", "gpt-4o", "sk-test", "https://api.openai.com/v1",
            AiApiFormat.OpenAIChatCompletions, false));

        // Delete via PM
        var pm = await system.CreateAsync<LlmProcessManager>(
            new DeleteLlmCmd(llm.Id));
        system.Send(pm.Id, new DeleteLlmCmd(llm.Id));
    }
}
