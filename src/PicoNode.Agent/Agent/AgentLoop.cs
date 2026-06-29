namespace PicoNode.Agent;

public sealed class AgentLoop
{
    private readonly IAgentLlm _llm;
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityRunner _runner;
    private readonly HookRunner _hookRunner;
    private const int MaxToolIterations = 20;

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
        if (messages.Count <= keepLast) return [.. messages];
        return messages.Skip(messages.Count - keepLast).ToList();
    }

    public async Task<List<Message>> RunTurnAsync(
        Session session,
        CancellationToken ct,
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null)
    {
        var result = new List<Message>();
        var iterations = 0;
        bool hasTools;

        do
        {
            iterations++;

            var messages = await session.BuildContext();
            var valid = messages.Where(m => !string.IsNullOrEmpty(m.Role)).ToArray();

            var context = new ChatContext { SystemPrompt = null, Messages = valid };
            var assistantMsg = await CallLLMAsync(context, ct, onEvent);

            if (assistantMsg == null) break;

            await session.AppendMessage(assistantMsg);
            result.Add(assistantMsg);

            var toolCallBlocks = assistantMsg.ContentBlocks
                ?.Where(cb => cb.Type == BlockTypeToolCall).ToArray();
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
                            ContentBlocks = [new ContentBlock { Type = BlockTypeText, Text = $"Tool not found: {tc.Name}" }],
                            IsError = true,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        };
                        await session.AppendMessage(notFound);
                        result.Add(notFound);
                        continue;
                    }

                    // Hook: OnToolCall
                    var hookInput = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(
                        new { kind = KindHook, eventName = HookEventToolCall, toolName = tc.Name, args = tc.Arguments });
                    var hookResult = await _hookRunner.EmitAsync(TriggerKind.OnToolCall, hookInput, ct);
                    if (hookResult is { } h && h.TryGetProperty("action", out var action) && action.GetString() == ActionBlock)
                        continue;

                    // Execute tool
                    var toolInput = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(
                        new { kind = KindToolCall, toolCallId = tc.Id, toolName = tc.Name, args = tc.Arguments });
                    var toolResponse = await _runner.ExecuteAsync(capConfig, KindToolCall, toolInput, ct);

                    var toolMsg = new Message
                    {
                        Role = RoleToolResult,
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        ContentBlocks = [new ContentBlock { Type = BlockTypeText, Text = toolResponse.TryGetProperty(FieldContent, out var c) ? c.ToString() : "" }],
                        IsError = toolResponse.TryGetProperty(FieldIsError, out var isErr) && isErr.GetBoolean(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    await session.AppendMessage(toolMsg);
                    result.Add(toolMsg);
                }
            }

            if (iterations >= MaxToolIterations) break;
        } while (hasTools);

        return result;
    }

    private async Task<Message?> CallLLMAsync(
        ChatContext context,
        CancellationToken ct,
        Func<AssistantMessageEvent, CancellationToken, ValueTask>? onEvent = null)
    {
        Message? finalMessage = null;
        var contentBlocks = new List<ContentBlock>();

        await foreach (var evt in _llm.StreamAsync(context.SystemPrompt, context.Messages, "default", null, ct))
        {
            switch (evt.Type)
            {
                case "text_delta":
                    contentBlocks.Add(new ContentBlock { Type = "text", Text = evt.Text ?? "" });
                    if (onEvent is not null)
                        await onEvent(new AssistantMessageEvent.TextDelta { Index = 0, Delta = evt.Text ?? "", Partial = new() }, ct);
                    break;
                case "done":
                    finalMessage = new Message { Role = "assistant", StopReason = evt.StopReason ?? "end_turn", ContentBlocks = [.. contentBlocks] };
                    break;
                case "error":
                    finalMessage = new Message { Role = "assistant", StopReason = "error", ErrorMessage = evt.ErrorMessage, ContentBlocks = [.. contentBlocks] };
                    break;
            }
        }
        return finalMessage;
    }

}
