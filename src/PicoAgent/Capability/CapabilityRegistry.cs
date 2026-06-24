namespace PicoAgent;

public sealed class CapabilityRegistry
{
    private readonly List<CapabilityConfig> _capabilities = [];

    public void Scan(string root)
    {
        var capsDir = Path.Combine(root, "capabilities");
        if (!Directory.Exists(capsDir)) return;

        foreach (var dir in Directory.EnumerateDirectories(capsDir, "*", SearchOption.AllDirectories))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            var manifest = ManifestLoader.LoadFromFile(manifestPath);
            if (manifest?.Capabilities == null) continue;

            foreach (var cap in manifest.Capabilities)
            {
                cap.Handler = Path.GetFullPath(Path.Combine(dir, cap.Handler));
                _capabilities.Add(cap);
            }
        }
    }

    public IReadOnlyList<CapabilityConfig> GetAll() => _capabilities;

    public IReadOnlyList<CapabilityConfig> GetByTrigger(TriggerKind kind)
        => _capabilities.Where(c => c.Triggers.Any(t => t.Kind == kind)).ToList();

    public IReadOnlyList<CapabilityConfig> GetByLifecycle(LifecycleKind lifecycle)
        => _capabilities.Where(c => c.Lifecycle == lifecycle).ToList();
}
