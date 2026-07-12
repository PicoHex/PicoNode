namespace PicoAgent;

public sealed class DynamicLLmClient : PicoNode.AI.ILLmClient
{
    private readonly PicoNode.Agent.Domain.Agent _agent;
    private readonly HttpClient? _httpClient;

    private static readonly HttpClient SharedClient = new(
        new SocketsHttpHandler { UseProxy = false }
    );

    public DynamicLLmClient(PicoNode.Agent.Domain.Agent agent) => _agent = agent;

    public DynamicLLmClient(PicoNode.Agent.Domain.Agent agent, HttpClient httpClient)
    {
        _agent = agent;
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<PicoNode.AI.AssistantMessageEvent> StreamAsync(
        PicoNode.AI.Model model,
        PicoNode.AI.Types.ChatContext context,
        PicoNode.AI.StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var llm = _agent.CurrentLlm;
        if (
            llm.ProviderName == "unconfigured"
            || string.IsNullOrEmpty(llm.ApiKey)
            || llm.ApiKey == "sk-test"
        )
        {
            yield return DoneMsg("[No API key configured. Please set up a provider in Settings.]");
            yield break;
        }

        var http = _httpClient ?? SharedClient;
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

        PicoNode.AI.ILLmClient? client = null;
        Exception? initError = null;
        try
        {
            client =
                llm.ApiFormat == AiApiFormat.AnthropicMessages
                    ? new AnthropicLLmClient(http)
                    : new OpenAILlmClient(http);
        }
        catch (Exception ex)
        {
            ExceptionHandler.LogOnly(ex, "DynamicLLmClient.cs");
            initError = ex;
        }

        if (initError is not null || client is null)
        {
            yield return DoneMsg($"[Error: {initError?.Message ?? "unknown"}]");
            yield break;
        }

        await using var enumerator = client
            .StreamAsync(realModel, context, realOptions, ct)
            .GetAsyncEnumerator(ct);

        while (true)
        {
            Exception? moveError = null;
            bool hasNext;

            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExceptionHandler.LogOnly(ex, "DynamicLLmClient.cs");
                moveError = ex;
                hasNext = false;
            }

            if (moveError is not null)
            {
                yield return DoneMsg($"[LLM error: {moveError.Message}]");
                yield break;
            }

            if (!hasNext)
                yield break;

            yield return enumerator.Current;
        }
    }

    private static PicoNode.AI.AssistantMessageEvent.Done DoneMsg(string text) =>
        new()
        {
            Message = new PicoNode.AI.Message
            {
                Role = "assistant",
                StopReason = "end_turn",
                ContentBlocks = [new PicoNode.AI.ContentBlock { Type = "text", Text = text }],
            },
        };
}
