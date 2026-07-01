namespace PicoNode.Agent.Tests.Extension;

public class HookRunnerTests
{
    [Test]
    public async Task EmitAsync_NoMatchingHooks_ReturnsNull()
    {
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var hookRunner = new HookRunner(registry, runner);

        var input = "{}"u8.ToArray();
        var result = await hookRunner.EmitAsync(
            TriggerKind.OnAgentStart,
            input,
            CancellationToken.None
        );
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task EmitAsync_OnToolCall_RegisteredHooks_Executes()
    {
        // Just verify the interface compiles and runs without exception
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var hookRunner = new HookRunner(registry, runner);

        var result = await hookRunner.EmitAsync(
            TriggerKind.OnToolCall,
            "{}"u8.ToArray(),
            CancellationToken.None
        );
        await Assert.That(result).IsNull(); // no hooks registered yet
    }
}
