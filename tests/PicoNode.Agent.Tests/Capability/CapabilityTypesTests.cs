namespace PicoNode.Agent.Tests.Capability;

public class CapabilityTypesTests
{
    [Test]
    public async Task ManifestCapability_Construction_Works()
    {
        var cap = new ManifestCapability
        {
            Name = "bash",
            Handler = "bash tools/runner.sh",
            TriggerKinds = [0],
            TriggerToolNames = ["bash"],
        };

        await Assert.That(cap.Name).IsEqualTo("bash");
        await Assert.That(cap.MatchesTrigger(TriggerKind.OnToolCall)).IsTrue();
        await Assert.That(cap.LifecycleKind).IsEqualTo(LifecycleKind.Persistent);
    }

    [Test]
    public async Task ManifestCapability_NullToolName_ForAllTools()
    {
        var cap = new ManifestCapability
        {
            Name = "guard",
            TriggerKinds = [0],
            TriggerToolNames = [null],
        };

        await Assert.That(cap.TriggerToolNames[0]).IsNull();
        await Assert.That(cap.MatchesTrigger(TriggerKind.OnToolCall)).IsTrue();
    }

    [Test]
    public async Task TriggerKind_Has16Values()
    {
        var values = Enum.GetValues<TriggerKind>();
        await Assert.That(values.Length).IsEqualTo(16);
    }
}
