namespace PicoNode.Agent.Tests.Agent;

/// <summary>
/// Tests for Model.ThinkingEnabled → StreamOptions.Reasoning propagation
/// through AgentLoop, and for ThinkingCommand.Apply parsing.
/// Also tests that onEvent callback ValueTask is properly awaited.
/// </summary>
public sealed class AgentLoopThinkingTests
{
    /// <summary>
    /// When Model.ThinkingEnabled is true, AgentLoop should pass StreamOptions
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
            ThinkingEnabled = true,
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
            .Because(
                "AgentLoop should pass StreamOptions (not null) when Model.ThinkingEnabled is true"
            );

        await Assert
            .That(capturedOptions!.Reasoning)
            .IsNotNull()
            .Because(
                "StreamOptions.Reasoning should be set when Model.ThinkingEnabled is true"
            );
    }

    /// <summary>
    /// When Model.ThinkingEnabled is false, AgentLoop should pass null options.
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
            ThinkingEnabled = false,
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
            .Because(
                "AgentLoop should pass null StreamOptions when Model.ThinkingEnabled is false"
            );
    }

    // ── ThinkingCommand.Apply tests ──────────────────────────────────

    [Test]
    public async Task Command_thinking_on_sets_true_when_already_true()
    {
        var model = new Model { ThinkingEnabled = true };
        ThinkingCommand.Apply(model, "on");
        await Assert
            .That(model.ThinkingEnabled)
            .IsTrue()
            .Because("/thinking on when already true should stay true");
    }

    [Test]
    public async Task Command_thinking_bare_toggles()
    {
        var model = new Model { ThinkingEnabled = false };
        ThinkingCommand.Apply(model, "");
        await Assert
            .That(model.ThinkingEnabled)
            .IsTrue()
            .Because("bare /thinking should toggle from false to true");
        ThinkingCommand.Apply(model, "");
        await Assert
            .That(model.ThinkingEnabled)
            .IsFalse()
            .Because("bare /thinking should toggle from true to false");
    }

    [Test]
    public async Task Command_thinking_off_sets_false()
    {
        var model = new Model { ThinkingEnabled = true };
        ThinkingCommand.Apply(model, "off");
        await Assert
            .That(model.ThinkingEnabled)
            .IsFalse()
            .Because("/thinking off should set reasoning to false");
    }

    [Test]
    public async Task Command_thinking_true_sets_true()
    {
        var model = new Model { ThinkingEnabled = false };
        ThinkingCommand.Apply(model, "true");
        await Assert
            .That(model.ThinkingEnabled)
            .IsTrue()
            .Because("/thinking true should set reasoning to true");
        ThinkingCommand.Apply(model, "true");
        await Assert
            .That(model.ThinkingEnabled)
            .IsTrue()
            .Because("/thinking true twice should stay true");
    }

    [Test]
    public async Task Command_thinking_false_sets_false()
    {
        var model = new Model { ThinkingEnabled = true };
        ThinkingCommand.Apply(model, "false");
        await Assert
            .That(model.ThinkingEnabled)
            .IsFalse()
            .Because("/thinking false should set reasoning to false");
        ThinkingCommand.Apply(model, "false");
        await Assert
            .That(model.ThinkingEnabled)
            .IsFalse()
            .Because("/thinking false twice should stay false");
    }

    [Test]
    public async Task Command_thinking_unknown_arg_returns_error()
    {
        var model = new Model { ThinkingEnabled = false };
        var result = ThinkingCommand.Apply(model, "unknown");
        await Assert
            .That(result)
            .IsNotNull()
            .Because("unknown argument should return an error message");
        await Assert
            .That(model.ThinkingEnabled)
            .IsFalse()
            .Because("unknown argument should not change reasoning state");
    }

    [Test]
    public async Task Command_thinking_null_returns_error()
    {
        var model = new Model { ThinkingEnabled = false };
        var result = ThinkingCommand.Apply(model, null!);
        await Assert
            .That(result)
            .IsNotNull()
            .Because("null argument should return an error message");
        await Assert
            .That(model.ThinkingEnabled)
            .IsFalse()
            .Because("null argument should not change reasoning state");
    }

    // ── onEvent await bug test ───────────────────────────────────

    /// <summary>
    /// RED: onEvent callback must be awaited, not fire-and-forget.
    /// Uses a blocking TCS inside the callback to verify that the
    /// caller awaits the returned ValueTask.
    /// </summary>
    [Test]
    public async Task OnEvent_callback_is_awaited()
    {
        var blocker = new TaskCompletionSource();
        var callbackInvoked = new TaskCompletionSource();

        var mockLlm = new SequentialMockLLmClient(
            new AssistantMessageEvent.TextDelta
            {
                Index = 0,
                Delta = "Hello",
                Partial = new Message { Role = "assistant" },
            },
            new AssistantMessageEvent.Done
            {
                Message = new Message
                {
                    Role = "assistant",
                    ContentBlocks = [new ContentBlock { Type = "text", Text = "Hello" }],
                    StopReason = "end_turn",
                },
            }
        );

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

        var runTask = loop.RunTurnAsync(
            messages,
            CancellationToken.None,
            onEvent: async (evt, _) =>
            {
                callbackInvoked.TrySetResult();
                await blocker.Task; // Block until released
            }
        );

        // Wait for the callback to be invoked
        await callbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Yield to let any stale continuations drain
        await Task.Yield();

        // BUG CHECK: if onEvent ValueTask is not awaited, runTask completes
        // immediately because CallLLMAsync drops the ValueTask on the floor.
        // If awaited, runTask is blocked on the callback.
        await Assert
            .That(runTask.IsCompleted)
            .IsFalse()
            .Because(
                "RunTurnAsync must be blocked awaiting onEvent; "
                    + "if completed, CallLLMAsync is dropping the ValueTask"
            );

        // Release the callback
        blocker.TrySetResult();

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// Mock LLM client that yields a pre-defined sequence of events.
/// </summary>
file sealed class SequentialMockLLmClient : ILLmClient
{
    private readonly AssistantMessageEvent[] _events;

    public SequentialMockLLmClient(params AssistantMessageEvent[] events)
    {
        _events = events;
    }

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        foreach (var evt in _events)
        {
            yield return evt;
        }
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
