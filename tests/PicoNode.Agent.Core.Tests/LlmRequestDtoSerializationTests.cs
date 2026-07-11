using PicoJetson;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: OpenAI LLM request DTO serialization via PicoJetson.
/// </summary>
public sealed class LlmRequestDtoSerializationTests
{
    [Test]
    public async Task OpenAiChatRequest_BasicFields_RoundTrip()
    {
        var req = new OpenAiChatRequest
        {
            Model = "gpt-4o",
            MaxTokens = 4096,
            Stream = true,
            Messages = [new OpenAiMessage { Role = "user", Content = "Hello" }],
        };
        var json = JsonSerializer.Serialize(req);

        await Assert.That(json).Contains("\"model\"");
        await Assert.That(json).Contains("\"gpt-4o\"");
        await Assert.That(json).Contains("\"max_tokens\"");
        await Assert.That(json).Contains("4096");
        await Assert.That(json).Contains("\"stream\"");
        await Assert.That(json).Contains("true");
        await Assert.That(json).Contains("\"messages\"");
    }

    [Test]
    public async Task OpenAiMessage_AssistantWithToolCalls()
    {
        var msg = new OpenAiMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls =
            [
                new OpenAiToolCall
                {
                    Id = "call_123",
                    Function = new OpenAiToolCallFunction
                    {
                        Name = "read",
                        Arguments = """{"path":"/tmp/test.txt"}""",
                    },
                },
            ],
        };
        var json = JsonSerializer.Serialize(msg);

        await Assert.That(json).Contains("\"tool_calls\"");
        await Assert.That(json).Contains("\"call_123\"");
        await Assert.That(json).Contains("\"read\"");
    }

    [Test]
    public async Task OpenAiMessage_SystemRole()
    {
        var msg = new OpenAiMessage { Role = "system", Content = "You are a helpful assistant." };
        var json = JsonSerializer.Serialize(msg);

        await Assert.That(json).Contains("\"system\"");
        await Assert.That(json).Contains("\"content\"");
    }
}
