namespace PicoNode.Tests;

/// <summary>
/// Verifies that INode exposes an OnFault event.
/// </summary>
public sealed class NodeFaultEventTests
{
    [Test]
    public async Task INode_has_OnFault_event()
    {
        var eventInfo = typeof(INode).GetEvent("OnFault");
        await Assert.That(eventInfo).IsNotNull();
    }

    [Test]
    public async Task INode_OnFault_is_Action_NodeFault()
    {
        var eventInfo = typeof(INode).GetEvent("OnFault");
        await Assert.That(eventInfo!.EventHandlerType).IsEqualTo(typeof(Action<NodeFault>));
    }
}
