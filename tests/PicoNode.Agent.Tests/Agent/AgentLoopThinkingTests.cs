namespace PicoNode.Agent.Tests.Agent;

/// <summary>
/// Tests for Model.Reasoning → StreamOptions.Reasoning propagation
/// through AgentLoop, and for ThinkingCommand.Apply parsing.
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
            new()
            {
                Role = "user",
                Content = "Hello",
                Timestamp = 1,
            },
        };

        await loop.RunTurnAsync(messages, CancellationToken.None);

        await Assert
            .That(capturedOptions)
            .IsNotNull()
            .Because("AgentLoop should pass StreamOptions (not null) when Model.Reasoning is true");

        await Assert
            .That(capturedOptions!.Reasoning)
            .IsNotNull()
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
            new()
            {
                Role = "user",
                Content = "Hello",
                Timestamp = 1,
            },
        };

        await loop.RunTurnAsync(messages, CancellationToken.None);

        await Assert
            .That(capturedOptions)
            .IsNull()
            .Because("AgentLoop should pass null StreamOptions when Model.Reasoning is false");
    }

    // ── ThinkingCommand.Apply tests ──────────────────────────────────

    /// <summary>
    /// RED: /thinking on should SET reasoning to true, not toggle.
    /// </summary>
    [Test]
    public async Task Command_thinking_on_sets_true_when_already_true()
    {
        var model = new Model { Reasoning = true };
        ThinkingCommand.Apply(model, "on");
        await Assert
            .That(model.Reasoning)
            .IsTrue()
            .Because("/thinking on when already true should stay true");
    }

    /// <summary>
    /// RED: Bare /thinking should toggle reasoning state.
    /// </summary>
    [Test]
    public async Task Command_thinking_bare_toggles()
    {
        var model = new Model { Reasoning = false };
        ThinkingCommand.Apply(model, "");
        await Assert
            .That(model.Reasoning)
            .IsTrue()
            .Because("bare /thinking should toggle from false to true");

        ThinkingCommand.Apply(model, "");
        await Assert
            .That(model.Reasoning)
            .IsFalse()
            .Because("bare /thinking should toggle from true to false");
    }

    /// <summary>
    /// RED: /thinking off should always set reasoning to false.
    /// </summary>
    [Test]
    public async Task Command_thinking_off_sets_false()
    {
        var model = new Model { Reasoning = true };
        ThinkingCommand.Apply(model, "off");
        await Assert
            .That(model.Reasoning)
            .IsFalse()
            .Because("/thinking off should set reasoning to false");
    }

    /// <summary>
    /// RED: /thinking true should always set reasoning to true.
    /// </summary>
    [Test]
    public async Task Command_thinking_true_sets_true()
    {
        var model = new Model { Reasoning = false };
        ThinkingCommand.Apply(model, "true");
        await Assert
            .That(model.Reasoning)
            .IsTrue()
            .Because("/thinking true should set reasoning to true");

        ThinkingCommand.Apply(model, "true");
        await Assert
            .That(model.Reasoning)
            .IsTrue()
            .Because("/thinking true twice should stay true");
    }

    /// <summary>
    /// RED: /thinking false should always set reasoning to false.
    /// </summary>
    [Test]
    public async Task Command_thinking_false_sets_false()
    {
        var model = new Model { Reasoning = true };
        ThinkingCommand.Apply(model, "false");
        await Assert
            .That(model.Reasoning)
            .IsFalse()
            .Because("/thinking false should set reasoning to false");

        ThinkingCommand.Apply(model, "false");
        await Assert
            .That(model.Reasoning)
            .IsFalse()
            .Because("/thinking false twice should stay false");
    }

    /// <summary>
    /// RED: Unknown argument returns error message.
    /// </summary>
    [Test]
    public async Task Command_thinking_unknown_arg_returns_error()
    {
        var model = new Model { Reasoning = false };
        var result = ThinkingCommand.Apply(model, "unknown");
        await Assert
            .That(result)
            .IsNotNull()
            .Because("unknown argument should return an error message");
        await Assert
            .That(model.Reasoning)
            .IsFalse()
            .Because("unknown argument should not change reasoning state");
    }
}

/// <summary>
/// Mock LLM client that captures StreamOptions for verification.
/// </summary>
file sealed class CapturingLLmClient : ILLmClient
{
    public Action<
        Model,
        ChatContext,
        StreamOptions?,
        CancellationToken
    >? OnStreamAsync { get; set; }

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
