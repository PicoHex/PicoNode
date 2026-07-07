using PicoNode.AI;

namespace PicoAgent;

/// <summary>
/// ILLmClient that dynamically reads Llm configuration from the Agent
/// on every call. When providers change via POST /config, subsequent
/// LLM calls automatically use the new credentials — no restart needed.
/// </summary>
public sealed class DynamicLLmClient : PicoNode.AI.ILLmClient
{
    private readonly PicoNode.Agent.Domain.Agent _agent;

    public DynamicLLmClient(PicoNode.Agent.Domain.Agent agent) => _agent = agent;

    public async IAsyncEnumerable<PicoNode.AI.AssistantMessageEvent> StreamAsync(
        PicoNode.AI.Model model,
        PicoNode.AI.Types.ChatContext context,
        PicoNode.AI.StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var llm = _agent.CurrentLlm;
        var client = llm.ApiFormat == AiApiFormat.AnthropicMessages
            ? (ILLmClient)new AnthropicLLmClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }))
            : new OpenAILlmClient(new HttpClient(new SocketsHttpHandler { UseProxy = false }));

        var realModel = new PicoNode.AI.Model
        {
            Id = llm.ModelId,
            Provider = llm.ProviderName,
            BaseUrl = llm.BaseUrl,
            Api = llm.ApiFormat,
            MaxTokens = llm.MaxTokens,
        };

        var realOptions = new PicoNode.AI.StreamOptions
        {
            ApiKey = llm.ApiKey,
            MaxTokens = llm.MaxTokens,
            Reasoning = llm.ThinkingEnabled ? llm.ThinkingLevel : null,
        };

        await foreach (var evt in client.StreamAsync(realModel, context, realOptions, ct))
            yield return evt;
    }
}
