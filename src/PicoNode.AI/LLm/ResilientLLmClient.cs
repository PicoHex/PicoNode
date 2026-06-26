namespace PicoNode.AI;

public sealed class ResilientLLmClient : ILLmClient
{
    private readonly IProviderRouter _router;
    private readonly IReadOnlyDictionary<string, ICircuitBreaker> _breakers;
    private readonly IReadOnlyDictionary<string, ILLmClient> _clients;

    public ResilientLLmClient(
        IProviderRouter router,
        IReadOnlyDictionary<string, ICircuitBreaker> breakers,
        IReadOnlyDictionary<string, ILLmClient> clients
    )
    {
        _router = router;
        _breakers = breakers;
        _clients = clients;
    }

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var provider = _router.Resolve(model.Id, model.Api);
        if (provider == null)
            throw new InvalidOperationException($"No provider found for model '{model.Id}'");

        // Populate model with provider config
        var resolvedModel = new Model
        {
            Id = model.Id,
            Name = model.Name,
            Provider = provider.Name,
            Api = provider.ApiFormat,
            BaseUrl = provider.BaseUrl,
            ThinkingEnabled = model.ThinkingEnabled,
            Cost = model.Cost,
            ContextWindow = model.ContextWindow,
            MaxTokens = model.MaxTokens,
        };

        if (_breakers.TryGetValue(provider.Name, out var breaker) && !breaker.TryAcquire())
        {
            // v1: no failover. v2: use FailoverService.
            throw new InvalidOperationException($"Provider '{provider.Name}' circuit is open");
        }

        if (!_clients.TryGetValue(provider.Name, out var client))
            throw new InvalidOperationException(
                $"No client registered for provider '{provider.Name}'"
            );

        await foreach (var evt in client.StreamAsync(resolvedModel, context, options, ct))
            yield return evt;

        breaker?.RecordSuccess();
    }
}
