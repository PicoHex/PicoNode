namespace PicoNode.AI;

public sealed class ProviderRouter : IProviderRouter
{
    private IReadOnlyList<ProviderConfig> _providers;

    public ProviderRouter(IEnumerable<ProviderConfig> providers)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
    }

    public void UpdateProviders(IEnumerable<ProviderConfig> providers)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
    }

    public ProviderConfig? Resolve(string? model, AiApiFormat? preferredFormat)
    {
        if (_providers.Count == 0)
            return null;

        var candidates = preferredFormat.HasValue
            ? _providers.Where(p => p.ApiFormat == preferredFormat.Value).ToList()
            : _providers;

        // Fall back to all providers if format filter yields no results
        if (candidates.Count == 0)
            candidates = _providers;

        var match =
            candidates.FirstOrDefault(p => model != null && p.ModelMapping.ContainsKey(model))
            ?? candidates.FirstOrDefault();

        return match;
    }
}
