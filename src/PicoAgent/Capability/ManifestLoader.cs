namespace PicoAgent;

// Flat DTOs — avoid nested object arrays that trigger PicoJetson SG hintName conflicts.

public sealed class ManifestData
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public ManifestCapability[] Capabilities { get; set; } = [];
    public string[] Knowledge { get; set; } = [];
    public string? Setup { get; set; }
}

public sealed class ManifestCapability
{
    public string Name { get; set; } = "";
    public string Handler { get; set; } = "";
    // Flat arrays instead of nested object arrays
    public int[] TriggerKinds { get; set; } = [];
    public string?[] TriggerToolNames { get; set; } = [];
    public int Lifecycle { get; set; }
    public string? SchemaPath { get; set; }
    public int Priority { get; set; } = 50;
    public string Description { get; set; } = "";

    public LifecycleKind LifecycleKind => (LifecycleKind)Lifecycle;
    public bool MatchesTrigger(TriggerKind kind)
        => TriggerKinds.Any(k => (TriggerKind)k == kind);
}

public static class ManifestLoader
{
    public static ManifestData? LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var bytes = Encoding.UTF8.GetBytes(json);
        return PicoJetson.JsonSerializer.Deserialize<ManifestData>(bytes);
    }
}
