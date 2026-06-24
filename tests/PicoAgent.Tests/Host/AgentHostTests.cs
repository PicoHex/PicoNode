namespace PicoAgent.Tests.Host;

using PicoNode.AI;
using PicoAgent;

public class AgentHostTests
{
    [Test]
    public async Task ProcessMessage_SimpleText_ReturnsAssistantResponse()
    {
        var llmClient = new MockLLmClient();
        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var model = new Model
        {
            Id = "claude-sonnet-4",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
            Provider = "anthropic",
            MaxTokens = 4096,
        };
        var loop = new AgentLoop(llmClient, registry, runner, model);
        var host = new AgentHost(loop, registry);

        var response = await host.ProcessMessageAsync("Hello", CancellationToken.None);

        await Assert.That(response).Contains("Hello!");
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
