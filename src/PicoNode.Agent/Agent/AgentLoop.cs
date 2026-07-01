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
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
    ) => RunTurnAsync(session, ModelId, SystemPrompt, ct, onEvent);

    public async Task<List<Message>> RunTurnAsync(
        Session session,
        string modelId,
        string? systemPrompt,
        CancellationToken ct,
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
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
            var assistantMsg = await CallLLMAsync(context, modelId, ct, onEvent);

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
                        new
                        {
                            kind = KindHook,
                            eventName = HookEventToolCall,
                            toolName = tc.Name,
                            args = tc.Arguments,
                        }
                    );
                    var hookResult = await _hookRunner.EmitAsync(
                        TriggerKind.OnToolCall,
                        hookInput,
                        ct
                    );
                    if (
                        hookResult is { } h
                        && h.TryGetProperty("action", out var action)
                        && action.GetString() == ActionBlock
                    )
                        continue;

                    // Execute tool
                    var toolInput = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            kind = KindToolCall,
                            toolCallId = tc.Id,
                            toolName = tc.Name,
                            args = tc.Arguments,
                        }
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
                                Text = toolResponse.TryGetProperty(FieldContent, out var c)
                                    ? c.ToString()
                                    : "",
                            },
                        ],
                        IsError =
                            toolResponse.TryGetProperty(FieldIsError, out var isErr)
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
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null
    )
    {
        Message? finalMessage = null;
        var contentBlocks = new List<ContentBlock>();
        var textAccum = new StringBuilder();

        await foreach (
            var evt in _llm.StreamAsync(context.SystemPrompt, context.Messages, modelId, null, ct)
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
