namespace PicoNode.Agent;

public sealed class AgentLoop
{
    private readonly IAgentLlm _llm;
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityRunner _runner;
    private readonly HookRunner _hookRunner;
    private const int MaxToolIterations = 20;

    public string? SystemPrompt { get; set; }
    public string ModelId { get; set; } = "default";

    /// <summary>
    /// Per-LLM-call timeout. Defaults to 5 minutes so misbehaving upstreams that
    /// stream one byte and then hang cannot pin server workers indefinitely.
    /// Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable.
    /// </summary>
    public TimeSpan LlmTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public AgentLoop(IAgentLlm llm, CapabilityRegistry registry, CapabilityRunner runner)
    {
        _llm = llm;
        _registry = registry;
        _runner = runner;
        _hookRunner = new HookRunner(registry, runner);
    }

    /// <summary>LLM-based summary compaction — v2 target.</summary>
    public static List<Message> Compact(List<Message> messages, int keepLast = 20)
    {
        if (messages.Count <= keepLast)
            return [.. messages];
        return messages.Skip(messages.Count - keepLast).ToList();
    }

    public Task<List<Message>> RunTurnAsync(
        Session session,
        CancellationToken ct,
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null,
        string? reasoningLevel = null
    ) => RunTurnAsync(session, ModelId, SystemPrompt, ct, onEvent, reasoningLevel);

    public async Task<List<Message>> RunTurnAsync(
        Session session,
        string modelId,
        string? systemPrompt,
        CancellationToken ct,
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null,
        string? reasoningLevel = null
    )
    {
        var result = new List<Message>();
        var iterations = 0;
        bool hasTools;

        do
        {
            iterations++;

            var messages = await session.BuildContext();
            var valid = messages.Where(m => !string.IsNullOrEmpty(m.Role)).ToArray();

            var context = new ChatContext { SystemPrompt = systemPrompt, Messages = valid };
            var assistantMsg = await CallLLMAsync(context, modelId, ct, onEvent, reasoningLevel);

            if (assistantMsg == null)
                break;

            await session.AppendMessage(assistantMsg);
            result.Add(assistantMsg);

            var toolCallBlocks = assistantMsg
                .ContentBlocks?.Where(cb => cb.Type == BlockTypeToolCall)
                .ToArray();
            hasTools = toolCallBlocks is { Length: > 0 };

            if (hasTools)
            {
                foreach (var tc in toolCallBlocks!)
                {
                    var capConfig = _registry.GetAll().FirstOrDefault(c => c.Name == tc.Name);

                    if (capConfig == null)
                    {
                        var notFound = new Message
                        {
                            Role = RoleToolResult,
                            ToolCallId = tc.Id,
                            ToolName = tc.Name,
                            ContentBlocks =
                            [
                                new ContentBlock
                                {
                                    Type = BlockTypeText,
                                    Text = $"Tool not found: {tc.Name}",
                                },
                            ],
                            IsError = true,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        };
                        await session.AppendMessage(notFound);
                        result.Add(notFound);
                        continue;
                    }

                    // Hook: OnToolCall
                    var hookInput = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(
                        new HookPayload { Kind = KindHook, EventName = HookEventToolCall, ToolName = tc.Name, Args = tc.Arguments }
                    );
                    var hookResult = await _hookRunner.EmitAsync(
                        TriggerKind.OnToolCall,
                        hookInput,
                        ct
                    );
                    if (
                        hookResult is { } h
                        && h.RootElement.TryGetProperty("action", out var action)
                        && action.GetString() == ActionBlock
                    )
                        continue;

                    // Execute tool
                    var toolInput = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(
                        new ToolCallPayload { Kind = KindToolCall, ToolCallId = tc.Id, ToolName = tc.Name, Args = tc.Arguments }
                    );
                    var toolResponse = await _runner.ExecuteAsync(
                        capConfig,
                        KindToolCall,
                        toolInput,
                        ct
                    );

                    var toolMsg = new Message
                    {
                        Role = RoleToolResult,
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        ContentBlocks =
                        [
                            new ContentBlock
                            {
                                Type = BlockTypeText,
                                Text = toolResponse.RootElement.TryGetProperty(FieldContent, out var c)
                                    ? c.ToString()
                                    : "",
                            },
                        ],
                        IsError =
                            toolResponse.RootElement.TryGetProperty(FieldIsError, out var isErr)
                            && isErr.GetBoolean(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    await session.AppendMessage(toolMsg);
                    result.Add(toolMsg);
                }
            }

            if (iterations >= MaxToolIterations)
                break;
        } while (hasTools);

        return result;
    }

    private async Task<Message?> CallLLMAsync(
        ChatContext context,
        string modelId,
        CancellationToken ct,
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null,
        string? reasoningLevel = null
    )
    {
        Message? finalMessage = null;
        var contentBlocks = new List<ContentBlock>();
        var textAccum = new StringBuilder();

        // Design-review flaw #14: enforce a per-call timeout on the LLM stream so
        // a hanging upstream cannot pin a request forever.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (LlmTimeout > TimeSpan.Zero && LlmTimeout != Timeout.InfiniteTimeSpan)
            linkedCts.CancelAfter(LlmTimeout);
        var streamCt = linkedCts.Token;

        await foreach (
            var evt in _llm.StreamAsync(
                    context.SystemPrompt,
                    context.Messages,
                    modelId,
                    reasoningLevel,
                    streamCt
                )
                .WithCancellation(streamCt)
        )
        {
            switch (evt.Type)
            {
                case "text_delta":
                    textAccum.Append(evt.Text);
                    if (onEvent is not null)
                        await onEvent(
                            new AssistantMessageEvent.TextDelta
                            {
                                Index = 0,
                                Delta = evt.Text ?? "",
                                Partial = new(),
                            },
                            ct
                        );
                    break;
                case "thinking_delta":
                    if (onEvent is not null)
                        await onEvent(
                            new AssistantMessageEvent.ThinkingDelta
                            {
                                Index = 0,
                                Delta = evt.Text ?? "",
                            },
                            ct
                        );
                    break;
                case "tool_call_start":
                    if (onEvent is not null)
                        await onEvent(
                            new AssistantMessageEvent.ToolCallStart { Index = 0, Partial = new() },
                            ct
                        );
                    break;
                case "tool_call_delta":
                    if (onEvent is not null)
                        await onEvent(
                            new AssistantMessageEvent.ToolCallDelta
                            {
                                Index = 0,
                                Delta = evt.Text ?? "",
                                Partial = new(),
                            },
                            ct
                        );
                    break;
                case "tool_call_end":
                    if (onEvent is not null)
                        await onEvent(
                            new AssistantMessageEvent.ToolCallEnd
                            {
                                Index = 0,
                                Call = new ContentBlock
                                {
                                    Type = "tool_call",
                                    Id = evt.ToolCallId,
                                    Name = evt.ToolName,
                                },
                                Partial = new(),
                            },
                            ct
                        );
                    break;
                case "done":
                    contentBlocks.Insert(
                        0,
                        new ContentBlock { Type = "text", Text = textAccum.ToString() }
                    );
                    finalMessage = new Message
                    {
                        Role = "assistant",
                        StopReason = evt.StopReason ?? "end_turn",
                        ContentBlocks = [.. contentBlocks],
                    };
                    break;
                case "error":
                    contentBlocks.Insert(
                        0,
                        new ContentBlock { Type = "text", Text = textAccum.ToString() }
                    );
                    finalMessage = new Message
                    {
                        Role = "assistant",
                        StopReason = "error",
                        ErrorMessage = evt.ErrorMessage,
                        ContentBlocks = [.. contentBlocks],
                    };
                    if (onEvent is not null)
                        await onEvent(
                            new AssistantMessageEvent.Error { Message = finalMessage },
                            ct
                        );
                    break;
            }
        }
        return finalMessage;
    }
}

/// <summary>
/// Payload for hook events (OnToolCall). Serialized to JSON and passed to capability hooks.
/// </summary>
[PicoSerializable]
[JsonCamelCase]
internal sealed class HookPayload
{
    public string Kind { get; set; } = "";
    public string EventName { get; set; } = "";
    public string? ToolName { get; set; }
    public Dictionary<string, object?> Args { get; set; } = new();
}

/// <summary>
/// Payload for tool execution requests. Serialized to JSON and passed to capability handlers.
/// </summary>
[PicoSerializable]
[JsonCamelCase]
internal sealed class ToolCallPayload
{
    public string Kind { get; set; } = "";
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public Dictionary<string, object?> Args { get; set; } = new();
}
