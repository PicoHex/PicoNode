using System.Runtime.CompilerServices;
using NetAI = PicoNode.AI;
using NetAITypes = PicoNode.AI.Types;

namespace PicoNode.Agent.Domain;

public sealed class LlmClientAdapter : ILlmClient
{
    private readonly NetAI.ILLmClient _inner;

    public LlmClientAdapter(NetAI.ILLmClient inner) => _inner = inner;

    public async Task<Message> CompleteAsync(
        Llm llm, List<Message> context, IReadOnlyList<Tool> tools, CancellationToken ct)
    {
        NetAI.ContentBlock[]? blocks = null;
        var stopReason = "end_turn";
        string? errorMsg = null;
        string? reasoningContent = null;

        await foreach (var evt in StreamInner(llm, context, tools, ct))
        {
            switch (evt)
            {
                case NetAI.AssistantMessageEvent.Done d:
                    blocks = d.Message.ContentBlocks;
                    stopReason = d.Message.StopReason ?? stopReason;
                    reasoningContent = d.Message.ReasoningContent;
                    break;
                case NetAI.AssistantMessageEvent.ThinkingDelta td:
                    reasoningContent = (reasoningContent ?? "") + td.Delta;
                    break;
                case NetAI.AssistantMessageEvent.Error e:
                    errorMsg = e.Message.ErrorMessage ?? "Unknown error";
                    break;
            }
        }

        if (errorMsg is not null)
            blocks ??= [new NetAI.ContentBlock { Type = "text", Text = $"[API Error: {errorMsg}]" }];

        return new Message
        {
            Role = "assistant",
            ContentBlocks = blocks ?? [],
            StopReason = stopReason,
            ReasoningContent = reasoningContent,
        };
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Llm llm, List<Message> context, IReadOnlyList<Tool> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in StreamInner(llm, context, tools, ct))
        {
            switch (evt)
            {
                case NetAI.AssistantMessageEvent.Start s:
                    yield return new StreamEvent { Type = "start" };
                    break;
                case NetAI.AssistantMessageEvent.TextDelta td:
                    yield return new StreamEvent { Type = "text", Content = td.Delta };
                    break;
                case NetAI.AssistantMessageEvent.ThinkingDelta tdd:
                    yield return new StreamEvent { Type = "thinking", Content = tdd.Delta };
                    break;
                case NetAI.AssistantMessageEvent.ToolCallStart ts:
                    yield return new StreamEvent
                    {
                        Type = "tool_call_start",
                        ToolCallId = ts.Index.ToString(),
                    };
                    break;
                case NetAI.AssistantMessageEvent.ToolCallDelta tcd:
                    yield return new StreamEvent
                    {
                        Type = "tool_call_delta",
                        ToolCallId = tcd.Index.ToString(),
                        Content = tcd.Delta,
                    };
                    break;
                case NetAI.AssistantMessageEvent.ToolCallEnd te:
                    yield return new StreamEvent
                    {
                        Type = "tool_call_end",
                        ToolCallId = te.Index.ToString(),
                        ToolName = te.Call?.Name ?? "",
                        Content = te.Call?.Arguments is { Count: > 0 } args
                            ? "{" + string.Join(",", args.Select(kv => $"\"{kv.Key}\":{JsonValue(kv.Value)}")) + "}"
                            : "{}",
                    };
                    break;
                case NetAI.AssistantMessageEvent.ToolResult tr:
                    yield return new StreamEvent
                    {
                        Type = "tool_result",
                        Content = tr.Content,
                        ToolCallId = tr.ToolCallId,
                        ToolName = tr.ToolName,
                    };
                    break;
                case NetAI.AssistantMessageEvent.Done d:
                    yield return new StreamEvent { Type = "done" };
                    yield break;
                case NetAI.AssistantMessageEvent.Error e:
                    yield return new StreamEvent
                    {
                        Type = "error",
                        Content = e.Message.ErrorMessage ?? "Unknown error",
                    };
                    yield break;
            }
        }
    }

    private async IAsyncEnumerable<NetAI.AssistantMessageEvent> StreamInner(
        Llm llm, List<Message> context, IReadOnlyList<Tool> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var model = new NetAI.Model
        {
            Id = llm.ModelId, Provider = llm.ProviderName,
            BaseUrl = llm.BaseUrl, Api = llm.ApiFormat,
            MaxTokens = llm.MaxTokens,
            ThinkingEnabled = llm.ThinkingEnabled,
            ThinkingLevel = llm.ThinkingLevel,
        };

        var chatContext = new NetAITypes.ChatContext
        {
            Messages = context.Where(m => m.Role != "system").ToArray(),
        };

        // Extract system prompt from context messages
        var systemMsg = context.FirstOrDefault(m => m.Role == "system")?.Content;
        if (systemMsg is { Length: > 0 })
            chatContext.SystemPrompt = systemMsg;

        if (tools.Count > 0)
        {
            chatContext.Tools = tools.Select(t => new NetAITypes.ToolSchema
            {
                Function = new NetAITypes.ToolSchemaFunction
                {
                    Name = t.Name, Description = t.Description,
                    Parameters = t.InputSchema ?? "{}",
                },
            }).ToArray();
        }

        var options = new NetAI.StreamOptions
        {
            ApiKey = llm.ApiKey,
            MaxTokens = llm.MaxTokens,
            Reasoning = llm.ThinkingEnabled ? llm.ThinkingLevel : null,
        };

        await foreach (var evt in _inner.StreamAsync(model, chatContext, options, ct))
            yield return evt;
    }

    private static string JsonValue(object? v) => v switch
    {
        null => "null",
        string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
        bool b => b.ToString().ToLower(),
        _ => v.ToString() ?? "null",
    };
}
