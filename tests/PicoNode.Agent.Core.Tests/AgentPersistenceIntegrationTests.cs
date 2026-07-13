using PicoNode.Actor;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class AgentPersistenceIntegrationTests : IDisposable
{
    private readonly string _baseDir;

    public AgentPersistenceIntegrationTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"pico-int-{Guid.CreateVersion7():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    [Test]
    public async Task StopAndRebuild_RestoresState()
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
        var agentId = agent.Id;

        system.Send(agentId, new StartAgent());
        await Task.Delay(50);
        system.Send(agentId, new AddToolCmd(new Tool { Name = "bash" }));
        await Task.Delay(50);

        await system.StopAsync(agentId);
        await Task.Delay(100);

        // Rebuild from persistence
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

        var restored = await system2.GetAsync<DomainAgent>(agentId);
        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.SessionId).IsEqualTo(sessionId);
        await Assert.That(restored.Status).IsEqualTo(AgentStatus.Running);
        await Assert.That(restored.ToolsSnapshot.Count).IsEqualTo(1);
        await Assert.That(restored.ToolsSnapshot[0].Name).IsEqualTo("bash");
        await Assert.That(restored.Session).IsNotNull();
    }

    [Test]
    public async Task Rebuild_GetAgentAsync_InjectsSessionStorage()
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
        var agentId = agent.Id;
        await system.StopAsync(agentId);
        await Task.Delay(100);

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

        var restored = await system2.GetAgentAsync<DomainAgent>(agentId);
        await Assert.That(restored).IsNotNull();
        // Session should be injected with persistent storage
        await Assert.That(restored!.Session).IsNotNull();
        await Assert.That(restored.Session.Id).IsEqualTo(sessionId);

        // Verify it works — append a message
        await restored.Session.Append(
            new MessageEntry
            {
                Message = new Message { Role = "user", Content = "hi" },
            }
        );
        var ctx = await restored.Session.BuildContext();
        await Assert.That(ctx.Count).IsGreaterThan(0);
    }
}
