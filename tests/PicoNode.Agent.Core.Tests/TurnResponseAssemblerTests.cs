using PicoNode.AI;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class TurnResponseAssemblerTests
{
    [Test]
    public async Task Handle_TextDelta_AccumulatesIntoTextBlock()
    {
        var asm = new TurnResponseAssembler("t1");
        asm.Handle(new StreamEvent { Type = "text", Content = "Hello " });
        asm.Handle(new StreamEvent { Type = "text", Content = "world" });
        var msg = asm.Build();

        await Assert.That(msg.Role).IsEqualTo(MessageRole.Assistant);
        var textBlock = msg.ContentBlocks?.FirstOrDefault(b => b.Type == "text");
        await Assert.That(textBlock?.Text).IsEqualTo("Hello world");
        await Assert.That(asm.HasToolCalls).IsFalse();
    }

    [Test]
    public async Task Handle_ThinkingDelta_AccumulatesIntoReasoningContent()
    {
        var asm = new TurnResponseAssembler("t1");
        asm.Handle(new StreamEvent { Type = "thinking", Content = "plan" });
        asm.Handle(new StreamEvent { Type = "thinking", Content = " more" });
        var msg = asm.Build();

        await Assert.That(msg.ReasoningContent).IsEqualTo("plan more");
    }

    [Test]
    public async Task Handle_ToolCallDeltaThenEnd_BuildsToolCallBlockWithArgs()
    {
        var asm = new TurnResponseAssembler("t1");
        asm.Handle(
            new StreamEvent
            {
                Type = "tool_call_delta",
                ToolCallId = "0",
                Content = """{"cmd":"ls"}""",
            }
        );
        asm.Handle(new StreamEvent { Type = "tool_call_end", ToolCallId = "0", ToolName = "bash" });
        var msg = asm.Build();

        await Assert.That(asm.HasToolCalls).IsTrue();
        var tc = msg.ContentBlocks?.FirstOrDefault(b => b.Type == "tool_call");
        await Assert.That(tc?.Name).IsEqualTo("bash");
        await Assert.That(tc?.Id).IsEqualTo("t1-0");
        await Assert.That(tc?.Arguments["cmd"]?.ToString()).IsEqualTo("ls");
    }

    [Test]
    public async Task Handle_MultipleToolCalls_EachGetsDistinctId()
    {
        var asm = new TurnResponseAssembler("t1");
        asm.Handle(new StreamEvent { Type = "tool_call_delta", ToolCallId = "0", Content = "{}" });
        asm.Handle(new StreamEvent { Type = "tool_call_end", ToolCallId = "0", ToolName = "a" });
        asm.Handle(new StreamEvent { Type = "tool_call_delta", ToolCallId = "1", Content = "{}" });
        asm.Handle(new StreamEvent { Type = "tool_call_end", ToolCallId = "1", ToolName = "b" });
        var msg = asm.Build();

        var ids = msg.ContentBlocks!.Where(b => b.Type == "tool_call").Select(b => b.Id).ToArray();
        await Assert.That(ids).Contains("t1-0");
        await Assert.That(ids).Contains("t1-1");
    }

    [Test]
    public async Task Build_NoEvents_ReturnsEmptyAssistantMessage()
    {
        var asm = new TurnResponseAssembler("t1");
        var msg = asm.Build();

        await Assert.That(msg.Role).IsEqualTo(MessageRole.Assistant);
        await Assert.That(asm.HasToolCalls).IsFalse();
    }
}
