using PicoNode.Actor;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class AgentRestoreTests : IDisposable
{
    private readonly string _baseDir;

    public AgentRestoreTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"pico-rebuild-{Guid.CreateVersion7():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    /// <summary>
    /// Stores the agent ID to a known location so a restart can find it.
    /// </summary>
    private static async Task SaveAgentId(string dir, Guid agentId)
    {
        var path = Path.Combine(dir, "agent.id");
        await File.WriteAllTextAsync(path, agentId.ToString());
    }

    /// <summary>
    /// Tries to load a previously saved agent ID. Returns null if none found.
    /// </summary>
    private static Task<Guid?> LoadAgentId(string dir)
    {
        var path = Path.Combine(dir, "agent.id");
        if (!File.Exists(path))
            return Task.FromResult<Guid?>(null);
        var text = File.ReadAllText(path).Trim();
        return Guid.TryParse(text, out var id)
            ? Task.FromResult<Guid?>(id)
            : Task.FromResult<Guid?>(null);
    }

    [Test]
    public async Task AfterStop_Restart_RebuildsAgent()
    {
        var store = new JsonlEventStore(Path.Combine(_baseDir, "actors"));
        var system = new ActorSystem(store);
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();
        var sessionId = Guid.CreateVersion7();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llmClient, toolRunner)
        );

        // First launch: create and start agent
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
        await SaveAgentId(_baseDir, agent.Id);
        await system.StopAsync(agent.Id);
        await Task.Delay(100);

        // Simulate restart: new ActorSystem instance, load saved agent ID
        var system2 = new ActorSystem(store);
        system2.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llmClient, toolRunner)
        );

        var savedId = await LoadAgentId(_baseDir);
        await Assert.That(savedId).IsNotNull();

        var restored = await system2.GetAgentAsync<DomainAgent>(savedId!.Value, _baseDir);
        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Status).IsEqualTo(AgentStatus.Running);
    }

    [Test]
    public async Task NoSavedAgent_CreatesNew()
    {
        var store = new JsonlEventStore(Path.Combine(_baseDir, "actors"));
        var system = new ActorSystem(store);
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();
        var sessionId = Guid.CreateVersion7();

        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(llmClient, toolRunner)
        );

        // No saved agent ID — should create new
        var savedId = await LoadAgentId(_baseDir);
        await Assert.That(savedId).IsNull();

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
        await Assert.That(agent).IsNotNull();
    }
}
