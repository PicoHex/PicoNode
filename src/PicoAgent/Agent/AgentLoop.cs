namespace PicoAgent;

using PicoNode.AI;

public sealed class AgentLoop
{
    private readonly ILLmClient _llm;
    private readonly CapabilityRegistry _registry;
    private readonly CapabilityRunner _runner;
    private readonly Model _model;
    private const int MaxToolIterations = 20;

    public AgentLoop(ILLmClient llm, CapabilityRegistry registry, CapabilityRunner runner, Model model)
    {
        _llm = llm;
        _registry = registry;
        _runner = runner;
        _model = model;
    }

    /// v1: truncate to last N messages. v2: LLM-based summary.
    public static List<Message> Compact(List<Message> messages, int keepLast = 20)
    {
        if (messages.Count <= keepLast) return [..messages];
        return messages.Skip(messages.Count - keepLast).ToList();
    }

    // v1: exceptions propagate to caller. v2: wrap in agent-level error handling.
    public async Task<List<Message>> RunTurnAsync(
        List<Message> messages, CancellationToken ct)
    {
        var result = new List<Message>();
        var model = _model;

        var iterations = 0;
        bool hasTools;

        do
        {
            iterations++;

            // Call LLM
            var msgArr = new Message[messages.Count];
            messages.CopyTo(msgArr);

            var context = new ChatContext { Messages = msgArr };
            var assistantMsg = await CallLLMAsync(model, context, ct);

            if (assistantMsg == null) break;

            messages.Add(assistantMsg);
            result.Add(assistantMsg);

            // Check for tool calls in ContentBlocks
            var toolCallBlocks = assistantMsg.ContentBlocks?
                .Where(cb => cb.Type == "tool_call").ToArray();
            hasTools = toolCallBlocks is { Length: > 0 };

            if (hasTools)
            {
                foreach (var tc in toolCallBlocks!)
                {
                    var capConfig = _registry.GetAll()
                        .FirstOrDefault(c => c.Name == tc.Name);

                    if (capConfig == null)
                    {
                        messages.Add(new Message
                        {
                            Role = "toolResult",
                            ToolCallId = tc.Id,
                            ToolName = tc.Name,
                            ContentBlocks = [new ContentBlock { Type = "text", Text = $"Tool not found: {tc.Name}" }],
                            IsError = true,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        });
                        continue;
                    }

                    // Run hooks before tool execution
                    var hookInput = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        kind = "hook",
                        eventName = "on_tool_call",
                        toolName = tc.Name,
                        args = tc.Arguments,
                    });

                    var hookResult = await RunHooksAsync(
                        TriggerKind.OnToolCall, hookInput, ct);

                    if (hookResult is { } h && h.TryGetProperty("action", out var action))
                    {
                        if (action.GetString() == "block") break;
                    }

                    // Execute tool
                    var toolInput = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        kind = "tool_call",
                        toolCallId = tc.Id,
                        toolName = tc.Name,
                        args = tc.Arguments,
                    });

                    var toolResponse = await _runner.ExecuteAsync(
                        capConfig, "tool_call", toolInput, ct);

                    var toolMsg = new Message
                    {
                        Role = "toolResult",
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        ContentBlocks = [new ContentBlock
                        {
                            Type = "text",
                            Text = toolResponse.TryGetProperty("content", out var c) ? c.ToString() : "",
                        }],
                        IsError = toolResponse.TryGetProperty("isError", out var isErr) && isErr.GetBoolean(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    messages.Add(toolMsg);
                    result.Add(toolMsg);
                }
            }

            if (iterations >= MaxToolIterations) break;
        }
        while (hasTools);

        return result;
    }

    private async Task<Message?> CallLLMAsync(
        Model model, ChatContext context, CancellationToken ct)
    {
        Message? finalMessage = null;

        await foreach (var evt in _llm.StreamAsync(model, context, null, ct))
        {
            if (evt is AssistantMessageEvent.Done d)
                finalMessage = d.Message;
            else if (evt is AssistantMessageEvent.Error e)
                finalMessage = e.Message;
        }

        return finalMessage;
    }

    private async Task<JsonElement?> RunHooksAsync(
        TriggerKind trigger, byte[] input, CancellationToken ct)
    {
        var hooks = _registry.GetByTrigger(trigger)
            .OrderBy(c => c.Priority);

        foreach (var hook in hooks)
        {
            var result = await _runner.ExecuteAsync(hook, "hook", input, ct);
            if (result.TryGetProperty("action", out var action))
            {
                if (action.GetString() == "block")
                    return result;
            }
        }

        return null;
    }
}
