namespace PicoNode.AI;

public sealed class AgentLlmAdapter : IAgentLlm
{
    private readonly ILLmClient _client;
    private readonly string _provider;

    public AgentLlmAdapter(ILLmClient client, string provider = "unknown")
    {
        _client = client;
        _provider = provider;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        string? systemPrompt,
        Message[] messages,
        string modelId,
        string? reasoningLevel,
        CancellationToken ct)
    {
        var model = new Model
        {
            Id = modelId,
            Provider = _provider,
            Api = AiApiFormat.AnthropicMessages,
            MaxTokens = 4096,
        };

        var context = new ChatContext
        {
            SystemPrompt = systemPrompt,
            Messages = messages,
        };

        StreamOptions? options = null;
        if (reasoningLevel is not null && reasoningLevel != "off")
        {
            var parsed = Enum.TryParse<ThinkingLevel>(reasoningLevel, ignoreCase: true, out var level);
            options = new StreamOptions { Reasoning = parsed ? level : ThinkingLevel.Medium };
        }

        await foreach (var evt in _client.StreamAsync(model, context, options, ct))
        {
            switch (evt)
            {
                case AssistantMessageEvent.Start:
                    break; // stream start marker — no LlmStreamEvent equivalent
                case AssistantMessageEvent.TextDelta td:
                    yield return new LlmStreamEvent("text_delta", td.Delta, null, null);
                    break;
                case AssistantMessageEvent.ThinkingDelta th:
                    yield return new LlmStreamEvent("thinking_delta", th.Delta, null, null);
                    break;
                case AssistantMessageEvent.Done d:
                    yield return new LlmStreamEvent("done", null, d.Message.StopReason, null);
                    break;
                case AssistantMessageEvent.Error e:
                    yield return new LlmStreamEvent("error", null, null, e.Message.ErrorMessage);
                    break;
            }
        }
    }
}
