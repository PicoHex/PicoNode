using PicoJetson;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// Verify reasoning_content is preserved through serialization.
/// DeepSeek requires reasoning_content in previous assistant messages
/// when thinking mode is enabled.
/// </summary>
public sealed class ReasoningContentRoundTripTests
{
    [Test]
    public async Task AssistantMessage_WithReasoningAndToolCalls_SerializesReasoningContent()
    {
        var msg = new OpenAiMessage
        {
            Role = "assistant",
            Content = null,
            ReasoningContent = "Let me think about this step by step...",
            ToolCalls = new[]
            {
                new OpenAiToolCall
                {
                    Id = "call_1",
                    Type = "function",
                    Function = new OpenAiToolCallFunction
                    {
                        Name = "read",
                        Arguments = """{"path":"test.txt"}""",
                    },
                },
            },
        };

        var req = new OpenAiChatRequest
        {
            Model = "deepseek-chat",
            Messages = new[] { msg },
            Stream = true,
        };

        var json = JsonSerializer.Serialize(req);

        await Assert.That(json).Contains("reasoning_content");
        await Assert.That(json).Contains("Let me think about this step by step");
        // Content should be null when there are tool_calls
        await Assert.That(json).Contains("\"content\":null");
    }

    [Test]
    public async Task AssistantMessage_WithReasoning_NoToolCalls_SerializesReasoningContent()
    {
        var msg = new OpenAiMessage
        {
            Role = "assistant",
            Content = "Here is the answer",
            ReasoningContent = "Let me think...",
        };

        var req = new OpenAiChatRequest
        {
            Model = "deepseek-chat",
            Messages = new[] { msg },
            Stream = true,
        };

        var json = JsonSerializer.Serialize(req);

        await Assert.That(json).Contains("reasoning_content");
        await Assert.That(json).Contains("Let me think...");
    }
}
