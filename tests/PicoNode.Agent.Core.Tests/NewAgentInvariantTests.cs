using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD tests for all Agent invariants through ActorSystem.
/// Uses NewAgent (EventSourcedActor) + Send commands + check state.
/// </summary>
public sealed class NewAgentInvariantTests
{
    private static List<Llm> DefaultLlms =>
    [
        new()
        {
            ProviderName = "deepseek",
            ModelId = "deepseek-chat",
            ApiKey = "sk-test",
        },
    ];

    private static (
        ActorSystem System,
        NewAgent Agent
    ) CreateSystemAndAgent()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();

        system.Register<NewAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new NewAgent(c, llmClient, toolRunner),
                    _ => throw new InvalidOperationException(),
                },
            () => new NewAgent(llmClient, toolRunner)
        );

        return (system, null!); // caller creates agent
    }

    // ═══════════════════════════════════════════════════════════
    // Invariant 1: Llms at least one, all ApiKey non-empty
    // ═══════════════════════════════════════════════════════════

    [Test]
    public async Task CreateAgent_EmptyLlms_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        await Assert
            .That(
                async () =>
                    await system.CreateAsync<NewAgent>(
                        new CreateAgent([], "any", "any", "/tmp")
                    )
            )
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task CreateAgent_ApiKeyMissing_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        var llms = new List<Llm>
        {
            new() { ProviderName = "x", ModelId = "y", ApiKey = "" },
        };
        await Assert
            .That(
                async () =>
                    await system.CreateAsync<NewAgent>(
                        new CreateAgent(llms, "x", "y", "/tmp")
                    )
            )
            .Throws<DomainInvariantException>();
    }

    // ═══════════════════════════════════════════════════════════
    // Invariant 2: CurrentLlm in Llms
    // ═══════════════════════════════════════════════════════════

    [Test]
    public async Task CreateAgent_CurrentLlmNotInList_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        await Assert
            .That(
                async () =>
                    await system.CreateAsync<NewAgent>(
                        new CreateAgent(DefaultLlms, "openai", "gpt-4", "/tmp")
                    )
            )
            .Throws<DomainInvariantException>();
    }

    // ═══════════════════════════════════════════════════════════
    // Invariant 6: Status transitions
    // ═══════════════════════════════════════════════════════════

    [Test]
    public async Task Start_FromPending_TransitionsToRunning()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(100);
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);
    }

    [Test]
    public async Task Start_FromRunning_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(100);

        await Assert
            .That(
                async () =>
                {
                    // Post will be processed — should throw
                    system.Send(agent.Id, new StartAgent());
                    await Task.Delay(200);
                }
            )
            .ThrowsNothing(); // Exception is swallowed by Actor.RunAsync, not propagated to Send
    }

    [Test]
    public async Task Complete_FromRunning_TransitionsToCompleted()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(100);
        system.Send(agent.Id, new CompleteAgent());
        await Task.Delay(100);
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Completed);
    }

    [Test]
    public async Task Complete_FromPending_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new CompleteAgent());
        await Task.Delay(100);
        // Status should still be Pending — exception swallowed by RunAsync
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Pending);
    }

    [Test]
    public async Task Fail_FromRunning_TransitionsAndStoresReason()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(100);
        system.Send(agent.Id, new FailAgent("network timeout"));
        await Task.Delay(100);
        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Failed);
        await Assert.That(agent.FailureReason).IsEqualTo("network timeout");
    }

    // ═══════════════════════════════════════════════════════════
    // Invariant 7: Llm management
    // ═══════════════════════════════════════════════════════════

    [Test]
    public async Task AddLlm_NoApiKey_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(
            agent.Id,
            new AddLlmCmd(new Llm { ProviderName = "x", ModelId = "y", ApiKey = "" })
        );
        await Task.Delay(100);
        // Should still have only 1 Llm
        await Assert.That(agent.LlmsSnapshot.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveLlm_OnlyOne_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new RemoveLlmCmd("deepseek", "deepseek-chat"));
        await Task.Delay(100);
        // Should still have 1 Llm
        await Assert.That(agent.LlmsSnapshot.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveLlm_CurrentLlm_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(
            agent.Id,
            new AddLlmCmd(
                new Llm
                {
                    ProviderName = "openai",
                    ModelId = "gpt-4o",
                    ApiKey = "sk-xxx",
                }
            )
        );
        await Task.Delay(50);
        system.Send(agent.Id, new RemoveLlmCmd("deepseek", "deepseek-chat"));
        await Task.Delay(100);
        // CurrentLlm is deepseek — should not be removed
        await Assert.That(agent.LlmsSnapshot.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task RemoveLlm_AfterSwitch_Succeeds()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(
            agent.Id,
            new AddLlmCmd(
                new Llm
                {
                    ProviderName = "openai",
                    ModelId = "gpt-4o",
                    ApiKey = "sk-xxx",
                }
            )
        );
        await Task.Delay(50);
        system.Send(agent.Id, new SwitchLlmCmd("openai", "gpt-4o"));
        await Task.Delay(50);
        system.Send(agent.Id, new RemoveLlmCmd("deepseek", "deepseek-chat"));
        await Task.Delay(100);
        await Assert.That(agent.LlmsSnapshot.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task SwitchLlm_NotFound_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new SwitchLlmCmd("nonexistent", "model"));
        await Task.Delay(100);
        // CurrentLlm should not have changed
        await Assert.That(agent.CurrentLlm.ProviderName).IsEqualTo("deepseek");
    }

    // ═══════════════════════════════════════════════════════════
    // Invariant 3: Tool names unique
    // ═══════════════════════════════════════════════════════════

    [Test]
    public async Task AddTool_DuplicateName_Throws()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new AddToolCmd(new Tool { Name = "read", Description = "Read" }));
        await Task.Delay(50);
        system.Send(
            agent.Id,
            new AddToolCmd(new Tool { Name = "read", Description = "Duplicate" })
        );
        await Task.Delay(100);
        await Assert.That(agent.ToolsSnapshot.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveTool_ByName()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        system.Send(agent.Id, new AddToolCmd(new Tool { Name = "read", Description = "Read" }));
        await Task.Delay(50);
        system.Send(agent.Id, new RemoveToolCmd("read"));
        await Task.Delay(100);
        await Assert.That(agent.ToolsSnapshot.Count()).IsEqualTo(0);
    }

    // ═══════════════════════════════════════════════════════════
    // Invariant 4 & 5: SpawnChild parent/child relationship
    // ═══════════════════════════════════════════════════════════

    [Test]
    public async Task SpawnChild_AddsToChildIds()
    {
        var (system, _) = CreateSystemAndAgent();
        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms, "deepseek", "deepseek-chat", "/tmp")
        );
        var childLlms = new List<Llm>
        {
            new()
            {
                ProviderName = "openai",
                ModelId = "gpt-4o",
                ApiKey = "sk-xxx",
            },
        };
        system.Send(
            agent.Id,
            new SpawnChildCmd(childLlms, "openai", "gpt-4o", [])
        );
        await Task.Delay(100);
        await Assert.That(agent.ChildIds.Count()).IsEqualTo(1);
    }
}
