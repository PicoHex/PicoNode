using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;

namespace PicoNode.AI;

public sealed class ResilientLLmClient : ILLmClient
{
    private readonly IProviderRouter _router;
    private readonly IReadOnlyDictionary<string, ProviderConfig> _providers;
    private readonly IReadOnlyDictionary<string, ICircuitBreaker> _breakers;
    private readonly IReadOnlyDictionary<string, ILLmClient> _clients;
    private const int MaxRetries = 3;
    private const int BaseDelayMs = 2000;

    public ResilientLLmClient(
        IProviderRouter router,
        IReadOnlyDictionary<string, ProviderConfig> providers,
        IReadOnlyDictionary<string, ICircuitBreaker> breakers,
        IReadOnlyDictionary<string, ILLmClient> clients
    )
    {
        _router = router;
        _providers = providers;
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
        var acquisition = await AcquireStreamAsync(model, context, options, ct);
        var enumerator = acquisition.Enumerator;
        var breaker = acquisition.Breaker;
        Exception? midStreamFailure = null;
        bool sawErrorEvent = false;

        try
        {
            if (acquisition.HasFirst)
            {
                var first = enumerator.Current;
                if (first is AssistantMessageEvent.Error)
                    sawErrorEvent = true;
                yield return first;

                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        midStreamFailure = ex;
                        break;
                    }
                    if (!hasNext)
                        break;

                    var evt = enumerator.Current;
                    if (evt is AssistantMessageEvent.Error)
                        sawErrorEvent = true;
                    yield return evt;
                }
            }
        }
        finally
        {
            await SafeDisposeAsync(enumerator);
        }

        if (midStreamFailure is not null)
        {
            breaker?.RecordFailure();
            ExceptionDispatchInfo.Capture(midStreamFailure).Throw();
        }

        if (sawErrorEvent)
            breaker?.RecordFailure();
        else
            breaker?.RecordSuccess();
    }

    private async Task<StreamAcquisition> AcquireStreamAsync(
        Model model,
        ChatContext context,
        StreamOptions? options,
        CancellationToken ct
    )
    {
        var provider =
            _router.Resolve(model.Id, model.Api)
            ?? throw new InvalidOperationException($"No provider found for model '{model.Id}'");

        if (
            _breakers.TryGetValue(provider.Name, out var primaryBreaker)
            && !primaryBreaker.TryAcquire()
        )
        {
            if (TryFailover(provider.Name, out var fallback))
                provider = fallback!;
            else
                throw new InvalidOperationException(
                    $"Provider '{provider.Name}' circuit is open, no failover available"
                );
        }

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (!_clients.TryGetValue(provider.Name, out var client))
                throw new InvalidOperationException($"No client for '{provider.Name}'");

            var breaker = _breakers.GetValueOrDefault(provider.Name);
            var resolvedModel = ResolveModel(model, provider);
            var resolvedOptions = MergeOptions(options, provider);

            IAsyncEnumerator<AssistantMessageEvent>? enumerator = null;
            try
            {
                enumerator = client
                    .StreamAsync(resolvedModel, context, resolvedOptions, ct)
                    .GetAsyncEnumerator(ct);
                var hasFirst = await enumerator.MoveNextAsync();
                return new StreamAcquisition(enumerator, breaker, hasFirst);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (enumerator is not null)
                    await SafeDisposeAsync(enumerator);
                throw;
            }
            catch (Exception ex)
            {
                if (enumerator is not null)
                    await SafeDisposeAsync(enumerator);
                breaker?.RecordFailure();
                if (IsRetryable(ex) && attempt < MaxRetries)
                {
                    await Task.Delay(BaseDelayMs * (int)Math.Pow(2, attempt), ct);
                    continue;
                }
                throw;
            }
        }
        throw new InvalidOperationException("Retry loop exited without producing a stream");
    }

    private static async ValueTask SafeDisposeAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch
        {
            // Disposal failures during error/cancellation paths must not mask the real cause.
        }
    }

    private readonly record struct StreamAcquisition(
        IAsyncEnumerator<AssistantMessageEvent> Enumerator,
        ICircuitBreaker? Breaker,
        bool HasFirst
    );

    private bool TryFailover(string current, out ProviderConfig? fallback)
    {
        foreach (var kv in _clients)
        {
            if (kv.Key == current)
                continue;
            if (_breakers.TryGetValue(kv.Key, out var b) && !b.TryAcquire())
                continue;
            if (_providers.TryGetValue(kv.Key, out var pc))
            {
                fallback = pc;
                return true;
            }
        }
        fallback = null;
        return false;
    }

    private static Model ResolveModel(Model model, ProviderConfig provider) =>
        new()
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

    private static StreamOptions MergeOptions(StreamOptions? user, ProviderConfig provider)
    {
        var apiKey = !string.IsNullOrEmpty(user?.ApiKey)
            ? user!.ApiKey
            : (!string.IsNullOrEmpty(provider.ApiKey) ? provider.ApiKey : null);

        return new StreamOptions
        {
            ApiKey = apiKey,
            MaxTokens = user?.MaxTokens,
            Temperature = user?.Temperature,
            Reasoning = user?.Reasoning,
            ThinkingLevelMap = user?.ThinkingLevelMap,
        };
    }

    private static bool IsRetryable(Exception ex) =>
        ex switch
        {
            HttpRequestException httpEx => IsRetryableStatus(httpEx.StatusCode),
            TimeoutException => true,
            TaskCanceledException tce => tce.InnerException is TimeoutException,
            IOException => true,
            _ => false,
        };

    private static bool IsRetryableStatus(HttpStatusCode? code) =>
        code switch
        {
            null => true, // connection-level failure surfaced as HttpRequestException without a status
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ => false,
        };
}
