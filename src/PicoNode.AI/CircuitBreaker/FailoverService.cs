namespace PicoNode.AI;

public interface IFailoverService
{
    ProviderConfig? GetNextProvider(ProviderConfig current, IReadOnlyList<ProviderConfig> queue);

    void RecordFailover(ProviderConfig from, ProviderConfig to, string reason);
}

public sealed class FailoverService : IFailoverService
{
    private readonly IReadOnlyDictionary<string, ICircuitBreaker> _breakers;

    public FailoverService(IReadOnlyDictionary<string, ICircuitBreaker> breakers)
    {
        _breakers = breakers;
    }

    public ProviderConfig? GetNextProvider(
        ProviderConfig current,
        IReadOnlyList<ProviderConfig> queue
    )
    {
        foreach (var provider in queue.OrderBy(p => p.Priority))
        {
            if (provider.Name == current.Name)
                continue;
            if (_breakers.TryGetValue(provider.Name, out var cb) && !cb.TryAcquire())
                continue;
            return provider;
        }
        return null;
    }

    public void RecordFailover(ProviderConfig from, ProviderConfig to, string reason)
    {
        // Logging hook point — v1: no-op
    }
}
