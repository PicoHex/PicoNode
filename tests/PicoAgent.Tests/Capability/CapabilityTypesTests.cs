namespace PicoAgent.Tests.Capability;

using PicoAgent;

public class CapabilityTypesTests
{
    [Test]
    public async Task CapabilityTrigger_Construction_Works()
    {
        var trigger = new CapabilityTrigger
        {
            Kind = TriggerKind.OnToolCall,
            ToolName = "bash",
        };

        await Assert.That(trigger.Kind).IsEqualTo(TriggerKind.OnToolCall);
        await Assert.That(trigger.ToolName).IsEqualTo("bash");
    }

    [Test]
    public async Task CapabilityTrigger_NullToolName_ForAllTools()
    {
        var trigger = new CapabilityTrigger
        {
            Kind = TriggerKind.OnToolCall,
            ToolName = null,
        };

        await Assert.That(trigger.ToolName).IsNull();
    }

    [Test]
    public async Task TriggerKind_Has16Values()
    {
        var values = Enum.GetValues<TriggerKind>();
        await Assert.That(values.Length).IsEqualTo(16);
    }
}
