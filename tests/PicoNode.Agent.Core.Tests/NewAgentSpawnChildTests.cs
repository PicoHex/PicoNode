using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class NewAgentSpawnChildTests
{
    private static List<Llm> DefaultLlms() =>
    [new() { ProviderName = "deepseek", ModelId = "deepseek-chat", ApiKey = "sk-test" }];

    /// <summary>
    /// SpawnChild must create a child Agent in the ActorSystem registry.
    /// Parent.ChildIds must include the child.
    /// </summary>
    [Test]
    public async Task SpawnChild_CreatesChildInRegistry()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var llmClient = new FakeLlmClient();
        var toolRunner = new FakeToolRunner();

        // Also register child type
        system.Register<NewAgent>(
            cmd => cmd switch { CreateAgent c => new NewAgent(c, llmClient, toolRunner), _ => throw new InvalidOperationException() },
            () => new NewAgent(llmClient, toolRunner));

        var parent = await system.CreateAsync<NewAgent>(
            new CreateAgent(DefaultLlms(), "deepseek", "deepseek-chat", "/tmp"));

        var childLlms = new List<Llm> { new() { ProviderName = "openai", ModelId = "gpt-4o", ApiKey = "sk-xxx" } };
        system.Send(parent.Id, new SpawnChildCmd(childLlms, "openai", "gpt-4o", []));
        await Task.Delay(200);

        await Assert.That(parent.ChildIds.Count).IsEqualTo(1);

        // Child should be retrievable from the system
        var childId = parent.ChildIds[0];
        var child = await system.GetAsync<NewAgent>(childId);
        await Assert.That(child).IsNotNull();
        await Assert.That(child!.ParentId).IsEqualTo(parent.Id);
    }
}
