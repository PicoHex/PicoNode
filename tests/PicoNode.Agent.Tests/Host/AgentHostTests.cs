namespace PicoNode.Agent.Tests.Host;

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
        var host = new AgentHost(loop);

        var response = await host.ProcessMessageAsync("Hello", CancellationToken.None);

        await Assert.That(response).Contains("Hello!");
    }
}

public sealed class MockLLmClient : ILLmClient
{
    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield return new AssistantMessageEvent.Start
        {
            Partial = new Message { Role = "assistant" },
        };
        yield return new AssistantMessageEvent.TextDelta
        {
            Index = 0,
            Delta = "Hello!",
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

    [Test]
    public async Task ProcessMessage_Concurrent_NoExceptions()
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
        var host = new AgentHost(loop);

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var sessionId = $"session-{i}";
            tasks.Add(
                Task.Run(async () =>
                {
                    var response = await host.ProcessMessageAsync(
                        $"msg-{i}",
                        CancellationToken.None,
                        sessionId
                    );
                    await Assert.That(response).Contains("Hello!");
                })
            );
        }

        await Task.WhenAll(tasks);
    }

    [Test]
    public async Task Set_and_get_thinking_state_per_session()
    {
        var loop = new AgentLoop(
            new MockLLmClient(),
            new CapabilityRegistry(),
            new CapabilityRunner(),
            new Model { Id = "test", MaxTokens = 4096 }
        );
        var host = new AgentHost(loop);

        host.SetThinkingState("s1", enabled: false, ThinkingLevel.High);
        var state = host.GetThinkingState("s1", defaultEnabled: true, ThinkingLevel.Medium);

        await Assert.That(state.ThinkingEnabled).IsFalse();
        await Assert.That(state.ThinkingLevel).IsEqualTo(ThinkingLevel.High);
    }

    [Test]
    public async Task Restore_session_preserves_thinking_state()
    {
        var loop = new AgentLoop(
            new MockLLmClient(),
            new CapabilityRegistry(),
            new CapabilityRunner(),
            new Model { Id = "test", MaxTokens = 4096 }
        );
        var host = new AgentHost(loop);

        var data = new SessionData
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = "Hi",
                    Timestamp = 1,
                },
            ],
            ThinkingEnabled = false,
            ThinkingLevel = ThinkingLevel.XHigh,
        };
        host.RestoreSession("s1", data);

        var restored = host.GetSessionData("s1");
        await Assert.That(restored.ThinkingEnabled).IsFalse();
        await Assert.That(restored.ThinkingLevel).IsEqualTo(ThinkingLevel.XHigh);
        await Assert.That(restored.Messages.Count).IsEqualTo(1);
    }
}
