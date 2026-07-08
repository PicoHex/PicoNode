namespace PicoNode.Agent.Domain;

using NetAI = PicoNode.AI;
using NetAITypes = PicoNode.AI.Types;

public sealed class LlmClientAdapter : ILlmClient
{
    private readonly NetAI.ILLmClient _inner;

    public LlmClientAdapter(NetAI.ILLmClient inner)
    {
        _inner = inner;
    }

    public async Task<Message> CompleteAsync(
        Llm llm,
        List<Message> context,
        IReadOnlyList<Tool> tools,
        CancellationToken ct
    )
    {
        var model = new NetAI.Model
        {
            Id = llm.ModelId,
            Provider = llm.ProviderName,
            BaseUrl = llm.BaseUrl,
            Api = llm.ApiFormat,
            MaxTokens = llm.MaxTokens,
            ThinkingEnabled = llm.ThinkingEnabled,
            ThinkingLevel = llm.ThinkingLevel,
        };

        var chatContext = new NetAITypes.ChatContext { Messages = context.ToArray() };

        if (tools.Count > 0)
        {
            chatContext.Tools = tools
                .Select(t => new NetAITypes.ToolSchema
                {
                    Function = new NetAITypes.ToolSchemaFunction
                    {
                        Name = t.Name,
                        Description = t.Description,
                        Parameters = t.InputSchema ?? "{}",
                    },
                })
                .ToArray();
        }

        var options = new NetAI.StreamOptions
        {
            ApiKey = llm.ApiKey,
            MaxTokens = llm.MaxTokens,
            Reasoning = llm.ThinkingEnabled ? llm.ThinkingLevel : null,
        };

        NetAI.ContentBlock[]? blocks = null;
        var stopReason = "end_turn";
        string? errorMsg = null;

        await foreach (var evt in _inner.StreamAsync(model, chatContext, options, ct))
        {
            if (evt is NetAI.AssistantMessageEvent.Done d)
            {
                blocks = d.Message.ContentBlocks;
                stopReason = d.Message.StopReason ?? stopReason;
            }
            else if (evt is NetAI.AssistantMessageEvent.Error e)
            {
                errorMsg = e.Message.ErrorMessage ?? "Unknown error";
            }
        }

        if (errorMsg is not null)
            blocks ??= [new NetAI.ContentBlock { Type = "text", Text = $"[API Error: {errorMsg}]" }];

        return new Message
        {
            Role = "assistant",
            ContentBlocks = blocks ?? [],
            StopReason = stopReason,
        };
    }
}
