using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;
using DomainAgent = PicoNode.Agent.Domain.Agent;

namespace PicoNode.Agent.Core.Tests;

public sealed class AgentInvariantTests
{
    private static List<Llm> DefaultLlms() =>
        [
            new()
            {
                ProviderName = "deepseek",
                ModelId = "chat",
                ApiKey = "sk",
            },
        ];

    private static (ActorSystem System, DomainAgent Agent) Create()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        system.Register<DomainAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new DomainAgent(c),
                    _ => throw new InvalidOperationException(),
                },
            () => new DomainAgent(new FakeLlmClient(), new FakeToolRunner())
        );
        return (system, null!);
    }

    [Test]
    public async Task Create_EmptyLlms_Throws()
    {
        var (system, _) = Create();
        await Assert
            .That(async () =>
                await system.CreateAsync<DomainAgent>(new CreateAgent([], "a", "b", Guid.CreateVersion7()))
            )
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task Create_NoApiKey_Throws()
    {
        var (system, _) = Create();
        await Assert
            .That(async () =>
                await system.CreateAsync<DomainAgent>(
                    new CreateAgent([new() { ApiKey = "" }], "a", "a", Guid.CreateVersion7())
                )
            )
            .Throws<DomainInvariantException>();
    }

    [Test]
    public async Task Start_FromPending_Transitions()
    {
        var (system, _) = Create();
        var a = await system.CreateAsync<DomainAgent>(
            new CreateAgent(DefaultLlms(), "deepseek", "chat", Guid.CreateVersion7())
        );
        system.Send(a.Id, new StartAgent());
        await Task.Delay(100);
        await Assert.That(a.Status).IsEqualTo(AgentStatus.Running);
    }

    [Test]
    public async Task Complete_Transitions()
    {
        var (system, _) = Create();
        var a = await system.CreateAsync<DomainAgent>(
            new CreateAgent(DefaultLlms(), "deepseek", "chat", Guid.CreateVersion7())
        );
        system.Send(a.Id, new StartAgent());
        await Task.Delay(50);
        system.Send(a.Id, new CompleteAgent());
        await Task.Delay(100);
        await Assert.That(a.Status).IsEqualTo(AgentStatus.Completed);
    }

    [Test]
    public async Task RemoveLlm_OnlyOne_Throws()
    {
        var (system, _) = Create();
        var a = await system.CreateAsync<DomainAgent>(
            new CreateAgent(DefaultLlms(), "deepseek", "chat", Guid.CreateVersion7())
        );
        system.Send(a.Id, new RemoveLlmCmd("deepseek", "chat"));
        await Task.Delay(100);
        await Assert.That(a.LlmsSnapshot.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AddTool_Duplicate_Throws()
    {
        var (system, _) = Create();
        var a = await system.CreateAsync<DomainAgent>(
            new CreateAgent(DefaultLlms(), "deepseek", "chat", Guid.CreateVersion7())
        );
        system.Send(a.Id, new AddToolCmd(new Tool { Name = "read" }));
        await Task.Delay(50);
        system.Send(a.Id, new AddToolCmd(new Tool { Name = "read" }));
        await Task.Delay(100);
        await Assert.That(a.ToolsSnapshot.Count).IsEqualTo(1);
    }
}
