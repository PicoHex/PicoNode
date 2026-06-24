namespace PicoAgent;

public sealed class ManifestData
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<CapabilityConfig> Capabilities { get; set; } = [];
    public List<string> Knowledge { get; set; } = [];
    public string? Setup { get; set; }
}

public sealed class CapabilityConfig
{
    public string Name { get; set; } = "";
    public string Handler { get; set; } = "";
    public List<CapabilityTrigger> Triggers { get; set; } = [];
    public LifecycleKind Lifecycle { get; set; }
    public string? SchemaPath { get; set; }
    public int Priority { get; set; } = 50;
    public string Description { get; set; } = "";
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
