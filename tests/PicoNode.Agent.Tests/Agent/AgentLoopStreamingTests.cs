namespace PicoNode.Agent.Tests.Agent;

public class AgentLoopStreamingTests
{
    [Test]
    public async Task RunTurnAsync_WithAsyncCallback_StreamsEvents()
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

        var events = new List<AssistantMessageEvent>();
        var messages = new List<Message>
        {
            new()
            {
                Role = "user",
                Content = "Hello",
                Timestamp = 1,
            },
        };

        await loop.RunTurnAsync(
            messages,
            CancellationToken.None,
            onEvent: async (evt, ct) =>
            {
                await Task.CompletedTask;
                events.Add(evt);
            }
        );

        await Assert.That(events.Count).IsGreaterThan(0);
        var textEvents = events.OfType<AssistantMessageEvent.TextDelta>().ToArray();
        await Assert.That(textEvents.Length).IsEqualTo(1);
        await Assert.That(textEvents[0].Delta).IsEqualTo("Hello!");
    }
}
