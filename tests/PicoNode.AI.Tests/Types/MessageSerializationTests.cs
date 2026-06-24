namespace PicoNode.AI.Tests.Types;

using PicoNode.AI;

public class MessageSerializationTests
{
    [Test]
    public async Task Serialize_UserMessage_RoundTrip()
    {
        var msg = new Message
        {
            Role = "user",
            Content = "Hello, world!",
            Timestamp = 1719000000000,
        };

        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(msg);
        var restored = PicoJetson.JsonSerializer.Deserialize<Message>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Role).IsEqualTo("user");
        await Assert.That(restored.Content).IsEqualTo("Hello, world!");
    }

    [Test]
    public async Task Serialize_AssistantMessage_WithContentBlocks()
    {
        var msg = new Message
        {
            Role = "assistant",
            ContentBlocks = new ContentBlock[]
            {
                new() { Type = "text", Text = "I'll help you with that." },
                new()
                {
                    Type = "tool_call",
                    Id = "tc_001",
                    Name = "read",
                    Arguments = new() { ["path"] = "/foo" },
                },
            },
            Model = "claude-sonnet-4-20250514",
            Provider = "anthropic",
            Api = AiApiFormat.AnthropicMessages,
            Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 },
            StopReason = "tool_use",
            Timestamp = 1719000001000,
        };

        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(msg);
        var restored = PicoJetson.JsonSerializer.Deserialize<Message>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Role).IsEqualTo("assistant");
        await Assert.That(restored.ContentBlocks!.Length).IsEqualTo(2);
        await Assert.That(restored.ContentBlocks[0].Type).IsEqualTo("text");
        await Assert.That(restored.ContentBlocks[0].Text).IsEqualTo("I'll help you with that.");
        await Assert.That(restored.ContentBlocks[1].Id).IsEqualTo("tc_001");
        await Assert.That(restored.StopReason).IsEqualTo("tool_use");
    }

    [Test]
    public async Task Serialize_ToolResult_RoundTrip()
    {
        var msg = new Message
        {
            Role = "toolResult",
            ToolCallId = "tc_001",
            ToolName = "read",
            ContentBlocks = new[] { new ContentBlock { Type = "text", Text = "file contents..." } },
            IsError = false,
            Timestamp = 1719000002000,
        };

        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(msg);
        var restored = PicoJetson.JsonSerializer.Deserialize<Message>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Role).IsEqualTo("toolResult");
        await Assert.That(restored.ToolCallId).IsEqualTo("tc_001");
        await Assert.That(restored.IsError).IsFalse();
    }

    [Test]
    public async Task Serialize_ChatContext_RoundTrip()
    {
        var context = new ChatContext
        {
            SystemPrompt = "You are a helpful assistant.",
            Messages = new Message[]
            {
                new Message
                {
                    Role = "user", Content = "Hi", Timestamp = 1,
                },
            },
        };

        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(context);
        var restored = PicoJetson.JsonSerializer.Deserialize<ChatContext>(json);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.SystemPrompt).IsEqualTo("You are a helpful assistant.");
        await Assert.That(restored.Messages.Length).IsEqualTo(1);
    }
}
