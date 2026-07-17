namespace PicoNode.AI.Tests.Types;

public class AssistantMessageEventTests
{
    [Test]
    public async Task TextDelta_Partial_AccumulatesText()
    {
        var partial = new Message
        {
            Role = MessageRole.Assistant,
            Model = "claude-sonnet-4",
            ContentBlocks = new[]
            {
                new ContentBlock { Type = "text", Text = "Hel" },
            },
        };

        var evt = new AssistantMessageEvent.TextDelta
        {
            Index = 0,
            Delta = "lo",
            Partial = partial,
        };

        await Assert.That(evt.Index).IsEqualTo(0);
        await Assert.That(evt.Delta).IsEqualTo("lo");
        await Assert.That(evt.Partial.ContentBlocks![0].Type).IsEqualTo("text");
    }

    [Test]
    public async Task Done_ContainsFinalMessage()
    {
        var msg = new Message
        {
            Role = MessageRole.Assistant,
            ContentBlocks = new[]
            {
                new ContentBlock { Type = "text", Text = "Done!" },
            },
            StopReason = "end_turn",
        };
        var evt = new AssistantMessageEvent.Done { Message = msg };

        await Assert.That(evt.Message.StopReason).IsEqualTo("end_turn");
    }

    [Test]
    public async Task Error_ContainsErrorMessage()
    {
        var msg = new Message
        {
            Role = MessageRole.Assistant,
            ErrorMessage = "Rate limited",
            StopReason = "error",
        };
        var evt = new AssistantMessageEvent.Error { Message = msg };

        await Assert.That(evt.Message.ErrorMessage).IsEqualTo("Rate limited");
    }
}
