namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: When an agent is restored from a previous run (BuildAsync not called),
/// the ToolRunner must still have registered handlers for built-in tools.
/// </summary>
public sealed class AgentFactoryToolRunnerTests
{
    [Test]
    public async Task ToolRunner_HasHandlers_EvenWithoutBuildAsync()
    {
        // ── Arrange: factory with built-in tools, but skip BuildAsync ──
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var factory = new AgentFactory(system).WithBuiltInTools();
        factory.Register();

        // Simulate restore: call EnsureBuiltInToolHandlers without BuildAsync
        factory.EnsureBuiltInToolHandlers();

        var runner = factory.GetToolRunner();

        // ── Act & Assert: built-in tools work ──
        var result = await runner.ExecuteAsync(
            "read",
            new Dictionary<string, object?> { ["path"] = "/nonexistent" },
            CancellationToken.None
        );
        await Assert.That(result).Contains("File not found"); // proves handler exists

        var result2 = await runner.ExecuteAsync(
            "ls",
            new Dictionary<string, object?> { ["path"] = "/nonexistent" },
            CancellationToken.None
        );
        await Assert.That(result2).Contains("Directory not found"); // proves handler exists
    }

    [Test]
    public async Task ToolRunner_UnknownTool_ReturnsNotFound()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);
        var factory = new AgentFactory(system).WithBuiltInTools();
        factory.Register();
        factory.EnsureBuiltInToolHandlers();
        var runner = factory.GetToolRunner();

        var result = await runner.ExecuteAsync("nonexistent_tool", [], CancellationToken.None);
        await Assert.That(result).Contains("Tool not found");
    }
}
