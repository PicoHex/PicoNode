using PicoAgent;

namespace PicoNode.Tests;

public class SseInEventDeserializationTests
{
    [Test]
    public async Task Deserialize_DeltaEvent_ReturnsTextDelta()
    {
        var evt = PicoAgentSseParser.ParseForTest("""{"type":"delta","content":"Hello"}""");
        await Assert.That(evt).IsNotNull();
        await Assert.That(evt).IsTypeOf<AssistantMessageEvent.TextDelta>();
        await Assert.That(((AssistantMessageEvent.TextDelta)evt!).Delta).IsEqualTo("Hello");
    }

    [Test]
    public async Task Deserialize_ThinkingEvent_ReturnsThinkingDelta()
    {
        var evt = PicoAgentSseParser.ParseForTest(
            """{"type":"thinking","content":"reasoning..."}"""
        );
        await Assert.That(evt).IsNotNull();
        await Assert.That(evt).IsTypeOf<AssistantMessageEvent.ThinkingDelta>();
        await Assert
            .That(((AssistantMessageEvent.ThinkingDelta)evt!).Delta)
            .IsEqualTo("reasoning...");
    }

    [Test]
    public async Task Deserialize_DoneEvent_ReturnsDone()
    {
        var evt = PicoAgentSseParser.ParseForTest("""{"type":"done","stopReason":"end_turn"}""");
        await Assert.That(evt).IsNotNull();
        await Assert.That(evt).IsTypeOf<AssistantMessageEvent.Done>();
        await Assert
            .That(((AssistantMessageEvent.Done)evt!).Message.StopReason)
            .IsEqualTo("end_turn");
    }

    [Test]
    public async Task Deserialize_ErrorEvent_ReturnsError()
    {
        var evt = PicoAgentSseParser.ParseForTest("""{"type":"error","message":"broke"}""");
        await Assert.That(evt).IsNotNull();
        await Assert.That(evt).IsTypeOf<AssistantMessageEvent.Error>();
        await Assert
            .That(((AssistantMessageEvent.Error)evt!).Message.ErrorMessage)
            .IsEqualTo("broke");
    }

    [Test]
    public async Task Deserialize_ToolCallStartEvent_ReturnsToolCallStart()
    {
        var evt = PicoAgentSseParser.ParseForTest(
            """{"type":"tool_call_start","toolCallId":"abc","toolName":"read"}"""
        );
        await Assert.That(evt).IsNotNull();
        await Assert.That(evt).IsTypeOf<AssistantMessageEvent.ToolCallStart>();
        var tcs = (AssistantMessageEvent.ToolCallStart)evt!;
        await Assert.That(tcs.Partial.ContentBlocks![0].Id).IsEqualTo("abc");
        await Assert.That(tcs.Partial.ContentBlocks[0].Name).IsEqualTo("read");
    }

    [Test]
    public async Task Deserialize_ToolCallDeltaEvent_ReturnsToolCallDelta()
    {
        var evt = PicoAgentSseParser.ParseForTest(
            """{"type":"tool_call_delta","toolCallId":"abc","content":"args"}"""
        );
        await Assert.That(evt).IsNotNull();
        await Assert.That(evt).IsTypeOf<AssistantMessageEvent.ToolCallDelta>();
        var tcd = (AssistantMessageEvent.ToolCallDelta)evt!;
        await Assert.That(tcd.Delta).IsEqualTo("args");
        await Assert.That(tcd.Partial.ContentBlocks![0].Id).IsEqualTo("abc");
    }

    [Test]
    public async Task Deserialize_ToolCallEndEvent_ReturnsToolCallEnd()
    {
        var evt = PicoAgentSseParser.ParseForTest(
            """{"type":"tool_call_end","toolCallId":"abc"}"""
        );
        await Assert.That(evt).IsNotNull();
        await Assert.That(evt).IsTypeOf<AssistantMessageEvent.ToolCallEnd>();
        await Assert.That(((AssistantMessageEvent.ToolCallEnd)evt!).Call.Id).IsEqualTo("abc");
    }
}
