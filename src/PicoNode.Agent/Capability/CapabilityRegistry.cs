namespace PicoNode.Agent;

public sealed class CapabilityRegistry
{
    private readonly List<ManifestCapability> _capabilities = [];

    public void Scan(string root)
    {
        var capsDir = Path.Combine(root, CapabilitiesDir);
        if (!Directory.Exists(capsDir))
            return;

        foreach (
            var dir in Directory.EnumerateDirectories(capsDir, "*", SearchOption.AllDirectories)
        )
        {
            var manifestPath = Path.Combine(dir, ManifestFile);
            if (!File.Exists(manifestPath))
                continue;

            var manifest = ManifestLoader.LoadFromFile(manifestPath);
            if (manifest?.Capabilities == null)
                continue;

            foreach (var cap in manifest.Capabilities)
            {
                cap.Handler = Path.GetFullPath(Path.Combine(dir, cap.Handler));
                _capabilities.Add(cap);
            }
        }
    }

    public IReadOnlyList<ManifestCapability> GetAll() => _capabilities;

    public IReadOnlyList<ManifestCapability> GetByTrigger(TriggerKind kind) =>
        _capabilities.Where(c => c.MatchesTrigger(kind)).ToList();

    public IReadOnlyList<ManifestCapability> GetByLifecycle(LifecycleKind lifecycle) =>
        _capabilities.Where(c => c.LifecycleKind == lifecycle).ToList();
}
