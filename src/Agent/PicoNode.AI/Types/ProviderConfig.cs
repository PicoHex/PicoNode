namespace PicoNode.AI;

public sealed class ProviderConfig
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public AiApiFormat ApiFormat { get; set; }
    public Dictionary<string, string> ModelMapping { get; set; } = new();
    public int Priority { get; set; }
}
