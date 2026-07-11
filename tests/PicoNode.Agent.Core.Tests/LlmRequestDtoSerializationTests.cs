using System.Text;
using PicoJetson;
using PicoNode.AI.Types;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: OpenAI LLM request DTO serialization via PicoJetson.
/// Replaces hand-crafted JSON in OpenAILlmClient.BuildRequestJson.
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
    public async Task OpenAiChatRequest_WithTools()
    {
        var req = new OpenAiChatRequest
        {
            Model = "deepseek-chat",
            MaxTokens = 2000,
            Stream = true,
            Messages = [],
            Tools =
            [
                new OpenAiToolDef
                {
                    Type = "function",
                    Function = new OpenAiFunctionDef
                    {
                        Name = "read",
                        Description = "Read file contents",
                        Parameters =
                            """{"type":"object","properties":{"path":{"type":"string"}}}""",
                    },
                },
            ],
            ToolChoice = "auto",
        };
        var json = JsonSerializer.Serialize(req);

        await Assert.That(json).Contains("\"tools\"");
        await Assert.That(json).Contains("\"function\"");
        await Assert.That(json).Contains("\"read\"");
        await Assert.That(json).Contains("\"tool_choice\"");
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
                    Type = "function",
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
