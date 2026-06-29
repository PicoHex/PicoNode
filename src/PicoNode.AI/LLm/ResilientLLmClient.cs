namespace PicoNode.AI;

public sealed class ResilientLLmClient : ILLmClient
{
    private readonly IProviderRouter _router;
    private readonly IReadOnlyDictionary<string, ICircuitBreaker> _breakers;
    private readonly IReadOnlyDictionary<string, ILLmClient> _clients;
    private const int MaxRetries = 3;
    private const int BaseDelayMs = 2000;

    public ResilientLLmClient(
        IProviderRouter router,
        IReadOnlyDictionary<string, ICircuitBreaker> breakers,
        IReadOnlyDictionary<string, ILLmClient> clients)
    {
        _router = router;
        _breakers = breakers;
        _clients = clients;
    }

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model, ChatContext context, StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var provider = _router.Resolve(model.Id, model.Api);
        if (provider is null)
            throw new InvalidOperationException($"No provider found for model '{model.Id}'");

        if (_breakers.TryGetValue(provider.Name, out var breaker) && !breaker.TryAcquire())
        {
            if (TryFailover(provider.Name, out var fallback))
                provider = fallback;
            else
                throw new InvalidOperationException($"Provider '{provider.Name}' circuit is open, no failover available");
        }

        var resolvedModel = ResolveModel(model, provider);

        var events = new List<AssistantMessageEvent>();
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested) break;
            events.Clear();

            if (!_clients.TryGetValue(provider.Name, out var client))
                throw new InvalidOperationException($"No client for '{provider.Name}'");

            try
            {
                await foreach (var evt in client.StreamAsync(resolvedModel, context, options, ct))
                    events.Add(evt);

                breaker?.RecordSuccess();
                break;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < MaxRetries)
            {
                var delay = BaseDelayMs * (int)Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }
        }

        foreach (var evt in events)
            yield return evt;
    }

    private bool TryFailover(string current, out ProviderConfig? fallback)
    {
        foreach (var kv in _clients)
        {
            if (kv.Key == current) continue;
            if (_breakers.TryGetValue(kv.Key, out var b) && !b.TryAcquire()) continue;
            fallback = new ProviderConfig { Name = kv.Key };
            return true;
        }
        fallback = null;
        return false;
    }

    private static Model ResolveModel(Model model, ProviderConfig provider) => new()
    {
        Id = model.Id, Name = model.Name,
        Provider = provider.Name, Api = provider.ApiFormat,
        BaseUrl = provider.BaseUrl,
        ThinkingEnabled = model.ThinkingEnabled,
        Cost = model.Cost, ContextWindow = model.ContextWindow,
        MaxTokens = model.MaxTokens,
    };

    private static bool IsRetryable(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("rate") || msg.Contains("limit") || msg.Contains("429")
            || msg.Contains("500") || msg.Contains("502") || msg.Contains("503")
            || msg.Contains("504") || msg.Contains("timeout") || msg.Contains("Timed out")
            || msg.Contains("connection") || msg.Contains("network");
    }
}
