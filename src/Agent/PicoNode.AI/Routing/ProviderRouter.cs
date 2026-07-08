namespace PicoNode.AI;

public sealed class ProviderRouter : IProviderRouter
{
    // Reads/writes go through Volatile so a snapshot taken inside Resolve
    // observes a consistent list even if UpdateProviders races concurrently.
    private IReadOnlyList<ProviderConfig> _providers;

    public ProviderRouter(IEnumerable<ProviderConfig> providers)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
    }

    public void UpdateProviders(IEnumerable<ProviderConfig> providers)
    {
        var snapshot = providers.OrderBy(p => p.Priority).ToList();
        System.Threading.Volatile.Write(ref _providers, snapshot);
    }

    public ProviderConfig? Resolve(string? model, AiApiFormat? preferredFormat)
    {
        // Snapshot once — subsequent branches must all agree on the same list.
        var providers = System.Threading.Volatile.Read(ref _providers);
        if (providers.Count == 0)
            return null;

        var candidates = preferredFormat.HasValue
            ? providers.Where(p => p.ApiFormat == preferredFormat.Value).ToList()
            : providers;

        // Fall back to all providers if format filter yields no results
        if (candidates.Count == 0)
            candidates = providers;

        var match =
            candidates.FirstOrDefault(p => model != null && p.ModelMapping.ContainsKey(model))
            ?? candidates.FirstOrDefault();

        return match;
    }
}
