namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: SSE output format should use protocol-native event: field for multiplexing.
/// </summary>
public sealed class SseFormatTests
{
    [Test]
    public async Task BuildSseFrame_WithTurnId_IncludesEventLine()
    {
        var evt = new ActorOutputEvent("thinking", "hello", TurnId: "abc123");

        var frame = ServerSse.BuildFrame(evt);

        await Assert.That(frame).StartsWith("event: abc123\n");
        await Assert.That(frame).Contains("data: ");
        await Assert.That(frame).EndsWith("\n\n");
    }

    [Test]
    public async Task BuildSseFrame_WithoutTurnId_NoEventLine()
    {
        var evt = new ActorOutputEvent("thinking", "hello");

        var frame = ServerSse.BuildFrame(evt);

        await Assert.That(frame).DoesNotContain("event: ");
        await Assert.That(frame).StartsWith("data: ");
    }

    [Test]
    public async Task BuildSseFrame_DoneEvent_Simple()
    {
        var evt = new ActorOutputEvent("done", null, TurnId: "x1");

        var frame = ServerSse.BuildFrame(evt);

        await Assert.That(frame).StartsWith("event: x1\ndata: ");
        await Assert.That(frame).EndsWith("\n\n");
    }
}
