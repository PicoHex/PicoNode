using PicoNode.AI;

namespace PicoAgent;

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
        if (llm.ProviderName == "unconfigured" || string.IsNullOrEmpty(llm.ApiKey) || llm.ApiKey == "sk-test")
        {
            yield return DoneMsg("[No API key configured. Please set up a provider in Settings.]");
            yield break;
        }

        await foreach (var evt in CreateClientAndStream(llm, context, ct))
            yield return evt;
    }

    private static async IAsyncEnumerable<PicoNode.AI.AssistantMessageEvent> CreateClientAndStream(
        Llm llm, PicoNode.AI.Types.ChatContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        string? errorMsg = null;
        PicoNode.AI.ILLmClient? client = null;
        try
        {
            var handler = new SocketsHttpHandler { UseProxy = false };
            client = llm.ApiFormat == AiApiFormat.AnthropicMessages
                ? new AnthropicLLmClient(new HttpClient(handler))
                : new OpenAILlmClient(new HttpClient(handler));
        }
        catch (Exception ex) { errorMsg = ex.Message; }

        if (errorMsg is not null || client is null)
        {
            yield return DoneMsg($"[Error: {errorMsg ?? "unknown"}]");
            yield break;
        }

        var realModel = new PicoNode.AI.Model
        {
            Id = llm.ModelId, Provider = llm.ProviderName, BaseUrl = llm.BaseUrl,
            Api = llm.ApiFormat, MaxTokens = llm.MaxTokens,
        };
        var realOptions = new PicoNode.AI.StreamOptions
        {
            ApiKey = llm.ApiKey, MaxTokens = llm.MaxTokens,
            Reasoning = llm.ThinkingEnabled ? llm.ThinkingLevel : null,
        };

        PicoNode.AI.AssistantMessageEvent? errEvt = null;
        try { await foreach (var evt in client.StreamAsync(realModel, context, realOptions, ct)) yield return evt; }
        catch (Exception ex) { errEvt = DoneMsg($"[LLM error: {ex.Message}]"); }
        if (errEvt is not null) yield return errEvt;
    }

    private static PicoNode.AI.AssistantMessageEvent.Done DoneMsg(string text) => new()
    {
        Message = new PicoNode.AI.Message
        {
            Role = "assistant", StopReason = "end_turn",
            ContentBlocks = [new PicoNode.AI.ContentBlock { Type = "text", Text = text }],
        },
    };
}
