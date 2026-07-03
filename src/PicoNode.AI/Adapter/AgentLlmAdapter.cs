namespace PicoNode.AI;

public sealed class AgentLlmAdapter : IAgentLlm
{
    private readonly ILLmClient _client;
    private readonly IProviderRouter? _router;
    private readonly string _fallbackProvider;

    /// <summary>
    /// Preferred: pass an IProviderRouter so the adapter can resolve the real
    /// AiApiFormat / provider name / BaseUrl for the caller's modelId. Without
    /// this, Model.Api falls back to the enum default (AnthropicMessages) which
    /// mis-directs ResilientLLmClient when both Anthropic and OpenAI providers
    /// are configured.
    /// </summary>
    public AgentLlmAdapter(ILLmClient client, IProviderRouter router)
    {
        _client = client;
        _router = router;
        _fallbackProvider = "unknown";
    }

    /// <summary>
    /// Legacy constructor kept for existing test fixtures that stub ILLmClient
    /// directly and do not exercise routing.
    /// </summary>
    public AgentLlmAdapter(ILLmClient client, string provider = "unknown")
    {
        _client = client;
        _router = null;
        _fallbackProvider = provider;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        string? systemPrompt,
        Message[] messages,
        string modelId,
        string? reasoningLevel,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        // Ask the router which provider actually serves this modelId so we set
        // the correct AiApiFormat / BaseUrl. Without a router we retain the old
        // legacy default so existing single-provider callers keep working.
        var resolved = _router?.Resolve(modelId, null);
        var apiFormat = resolved?.ApiFormat ?? AiApiFormat.AnthropicMessages;
        var providerName = resolved?.Name ?? _fallbackProvider;
        var baseUrl = resolved?.BaseUrl ?? "";

        var model = new Model
        {
            Id = modelId,
            Provider = providerName,
            Api = apiFormat,
            BaseUrl = baseUrl,
            MaxTokens = 4096,
        };

        var context = new ChatContext { SystemPrompt = systemPrompt, Messages = messages };

        StreamOptions? options = null;
        if (reasoningLevel is not null && reasoningLevel != "off")
        {
            var parsed = Enum.TryParse<ThinkingLevel>(
                reasoningLevel,
                ignoreCase: true,
                out var level
            );
            options = new StreamOptions { Reasoning = parsed ? level : ThinkingLevel.Medium };
        }
        else
        {
            // Explicitly disable thinking so providers that default to enabled
            // (e.g. DeepSeek) respect the off state.
            options = new StreamOptions { ThinkingDisabled = true };
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
                case AssistantMessageEvent.ToolCallStart tcs:
                    yield return new LlmStreamEvent(
                        "tool_call_start",
                        Text: null,
                        StopReason: null,
                        ErrorMessage: null,
                        ToolCallId: null,
                        ToolName: null
                    );
                    break;
                case AssistantMessageEvent.ToolCallDelta tcd:
                    yield return new LlmStreamEvent(
                        "tool_call_delta",
                        Text: tcd.Delta,
                        StopReason: null,
                        ErrorMessage: null,
                        ToolCallId: null,
                        ToolName: null
                    );
                    break;
                case AssistantMessageEvent.ToolCallEnd tce:
                    yield return new LlmStreamEvent(
                        "tool_call_end",
                        Text: null,
                        StopReason: null,
                        ErrorMessage: null,
                        ToolCallId: tce.Call.Id,
                        ToolName: tce.Call.Name
                    );
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
