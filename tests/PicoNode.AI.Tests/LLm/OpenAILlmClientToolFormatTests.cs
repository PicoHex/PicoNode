namespace PicoNode.AI.Tests.LLm;

using System.Linq;
using System.Text.Json;
using PicoNode.AI;

/// <summary>
/// Batch 4 (A-OpenAI): OpenAILlmClient must emit tool_calls / tool messages in the
/// exact shape the OpenAI Chat Completions API expects, not the internal
/// PicoNode "toolResult" role + text-only block shape.
/// </summary>
public sealed class OpenAILlmClientToolFormatTests
{
    private static MockHttpHandler NewMockHandler() =>
        new()
        {
            NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n"),
            },
        };

    private static async Task DriveAsync(OpenAILlmClient client, ChatContext context)
    {
        await foreach (
            var _ in client.StreamAsync(
                new Model
                {
                    Id = "gpt-4o",
                    BaseUrl = "https://api.openai.com/v1",
                    Api = AiApiFormat.OpenAIChatCompletions,
                    MaxTokens = 4096,
                },
                context,
                null,
                CancellationToken.None
            )
        ) { }
    }

    [Test]
    public async Task BuildRequest_ToolResultMessage_UsesRoleToolAndToolCallId()
    {
        var handler = NewMockHandler();
        var client = new OpenAILlmClient(new HttpClient(handler));

        var context = new ChatContext
        {
            Messages =
            [
                new Message { Role = "user", Content = "hi", Timestamp = 1 },
                new Message
                {
                    Role = "assistant",
                    ContentBlocks =
                    [
                        new ContentBlock
                        {
                            Type = "tool_call",
                            Id = "call_abc",
                            Name = "get_weather",
                            Arguments = new() { ["city"] = "Tokyo" },
                        },
                    ],
                    Timestamp = 2,
                },
                new Message
                {
                    Role = "toolResult",
                    ToolCallId = "call_abc",
                    ToolName = "get_weather",
                    ContentBlocks =
                    [
                        new ContentBlock { Type = "text", Text = "sunny, 22C" },
                    ],
                    Timestamp = 3,
                },
            ],
        };

        await DriveAsync(client, context);

        await Assert.That(handler.CapturedRequestBody).IsNotNull();
        using var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");

        // Locate the tool result message (the one that carries a tool_call_id).
        JsonElement? toolMsg = null;
        foreach (var m in messages.EnumerateArray())
        {
            if (m.TryGetProperty("tool_call_id", out _))
            {
                toolMsg = m;
                break;
            }
        }

        await Assert.That(toolMsg.HasValue).IsTrue();
        await Assert.That(toolMsg!.Value.GetProperty("role").GetString()).IsEqualTo("tool");
        await Assert
            .That(toolMsg!.Value.GetProperty("tool_call_id").GetString())
            .IsEqualTo("call_abc");
        await Assert
            .That(toolMsg!.Value.GetProperty("content").GetString())
            .IsEqualTo("sunny, 22C");
    }

    [Test]
    public async Task BuildRequest_AssistantWithToolCallBlocks_EmitsToolCallsArray()
    {
        var handler = NewMockHandler();
        var client = new OpenAILlmClient(new HttpClient(handler));

        var context = new ChatContext
        {
            Messages =
            [
                new Message { Role = "user", Content = "weather?", Timestamp = 1 },
                new Message
                {
                    Role = "assistant",
                    ContentBlocks =
                    [
                        new ContentBlock { Type = "text", Text = "" },
                        new ContentBlock
                        {
                            Type = "tool_call",
                            Id = "call_xyz",
                            Name = "get_weather",
                            Arguments = new()
                            {
                                ["city"] = "Paris",
                                ["units"] = "metric",
                            },
                        },
                    ],
                    Timestamp = 2,
                },
            ],
        };

        await DriveAsync(client, context);

        using var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var assistant = messages.Last();

        await Assert.That(assistant.GetProperty("role").GetString()).IsEqualTo("assistant");
        await Assert.That(assistant.TryGetProperty("tool_calls", out var tc)).IsTrue();
        await Assert.That(tc.GetArrayLength()).IsEqualTo(1);

        var call = tc[0];
        await Assert.That(call.GetProperty("id").GetString()).IsEqualTo("call_xyz");
        await Assert.That(call.GetProperty("type").GetString()).IsEqualTo("function");
        var fn = call.GetProperty("function");
        await Assert.That(fn.GetProperty("name").GetString()).IsEqualTo("get_weather");

        // arguments must be a JSON-encoded string, not a nested object.
        var argsProp = fn.GetProperty("arguments");
        await Assert.That(argsProp.ValueKind).IsEqualTo(JsonValueKind.String);
        using var argsDoc = JsonDocument.Parse(argsProp.GetString()!);
        await Assert.That(argsDoc.RootElement.GetProperty("city").GetString()).IsEqualTo("Paris");
        await Assert
            .That(argsDoc.RootElement.GetProperty("units").GetString())
            .IsEqualTo("metric");
    }

    [Test]
    public async Task BuildRequest_ToolResultRole_DoesNotEmitInternalToolResultLiteral()
    {
        var handler = NewMockHandler();
        var client = new OpenAILlmClient(new HttpClient(handler));

        var context = new ChatContext
        {
            Messages =
            [
                new Message { Role = "user", Content = "hi", Timestamp = 1 },
                new Message
                {
                    Role = "assistant",
                    ContentBlocks =
                    [
                        new ContentBlock
                        {
                            Type = "tool_call",
                            Id = "call_1",
                            Name = "ping",
                            Arguments = new(),
                        },
                    ],
                    Timestamp = 2,
                },
                new Message
                {
                    Role = "toolResult",
                    ToolCallId = "call_1",
                    ContentBlocks = [new ContentBlock { Type = "text", Text = "pong" }],
                    Timestamp = 3,
                },
            ],
        };

        await DriveAsync(client, context);

        // The internal camelCase "toolResult" identifier must never leak into the wire payload.
        await Assert.That(handler.CapturedRequestBody!).DoesNotContain("\"role\":\"toolResult\"");
    }
}
