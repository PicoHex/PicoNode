namespace PicoAgent.Tests.Agent;

using PicoNode.AI;
using PicoAgent;

public class AgentLoopTests
{
    [Test]
    public async Task RunTurnAsync_SimpleMessage_ReturnsAssistantResponse()
    {
        var llmClient = new MockLLmClient();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var model = new Model
        {
            Id = "claude-sonnet-4-20250514",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
            Provider = "anthropic",
            MaxTokens = 4096,
        };
        var loop = new AgentLoop(llmClient, registry, runner, model);

        var messages = new List<Message>
        {
            new() { Role = "user", Content = "Hello", Timestamp = 1 },
        };

        var result = await loop.RunTurnAsync(messages, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsGreaterThan(0);
        var lastAssistant = result.LastOrDefault(m => m.Role == "assistant");
        await Assert.That(lastAssistant).IsNotNull();
        await Assert.That(lastAssistant!.ContentBlocks![0].Text).IsEqualTo("Hello!");
    }
}

public sealed class MockLLmClient : ILLmClient
{
    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model, ChatContext context, StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AssistantMessageEvent.Start
        {
            Partial = new Message { Role = "assistant" },
        };
        yield return new AssistantMessageEvent.TextDelta
        {
            Index = 0, Delta = "Hello!",
            Partial = new Message
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Hello!" }],
            },
        };
        yield return new AssistantMessageEvent.Done
        {
            Message = new Message
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Hello!" }],
                StopReason = "end_turn",
            },
        };
    }
}
