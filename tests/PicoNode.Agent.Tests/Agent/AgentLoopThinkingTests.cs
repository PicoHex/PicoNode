namespace PicoNode.Agent.Tests.Agent;

/// <summary>
/// Tests for Model.Reasoning → StreamOptions.Reasoning propagation
/// through AgentLoop.
/// </summary>
public sealed class AgentLoopThinkingTests
{
    /// <summary>
    /// RED: When Model.Reasoning is true, AgentLoop should pass StreamOptions
    /// with Reasoning set, not null.
    /// </summary>
    [Test]
    public async Task RunTurnAsync_WithReasoning_PassesStreamOptionsWithReasoning()
    {
        StreamOptions? capturedOptions = null;

        var mockLlm = new CapturingLLmClient
        {
            OnStreamAsync = (_, _, options, _) =>
            {
                capturedOptions = options;
            },
        };

        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var model = new Model
        {
            Id = "claude-sonnet-4",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
            Provider = "anthropic",
            MaxTokens = 4096,
            Reasoning = true,
        };

        var loop = new AgentLoop(mockLlm, registry, runner, model);
        var messages = new List<Message>
        {
            new() { Role = "user", Content = "Hello", Timestamp = 1 },
        };

        await loop.RunTurnAsync(messages, CancellationToken.None);

        await Assert.That(capturedOptions).IsNotNull()
            .Because("AgentLoop should pass StreamOptions (not null) when Model.Reasoning is true");

        await Assert.That(capturedOptions!.Reasoning).IsNotNull()
            .Because("StreamOptions.Reasoning should be set when Model.Reasoning is true");
    }

    /// <summary>
    /// RED: When Model.Reasoning is false, AgentLoop should pass null options
    /// (existing behavior preserved).
    /// </summary>
    [Test]
    public async Task RunTurnAsync_WithoutReasoning_PassesNullOptions()
    {
        StreamOptions? capturedOptions = null;

        var mockLlm = new CapturingLLmClient
        {
            OnStreamAsync = (_, _, options, _) =>
            {
                capturedOptions = options;
            },
        };

        var registry = new CapabilityRegistry();
        var runner = new CapabilityRunner();
        var model = new Model
        {
            Id = "claude-sonnet-4",
            BaseUrl = "https://api.anthropic.com",
            Api = AiApiFormat.AnthropicMessages,
            Provider = "anthropic",
            MaxTokens = 4096,
            Reasoning = false,
        };

        var loop = new AgentLoop(mockLlm, registry, runner, model);
        var messages = new List<Message>
        {
            new() { Role = "user", Content = "Hello", Timestamp = 1 },
        };

        await loop.RunTurnAsync(messages, CancellationToken.None);

        await Assert.That(capturedOptions).IsNull()
            .Because("AgentLoop should pass null StreamOptions when Model.Reasoning is false");
    }
}

/// <summary>
/// Mock LLM client that captures StreamOptions for verification.
/// </summary>
file sealed class CapturingLLmClient : ILLmClient
{
    public Action<Model, ChatContext, StreamOptions?, CancellationToken>? OnStreamAsync { get; set; }

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        OnStreamAsync?.Invoke(model, context, options, ct);
        yield return new AssistantMessageEvent.Done
        {
            Message = new Message
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "ok" }],
                StopReason = "end_turn",
            },
        };
    }
}
