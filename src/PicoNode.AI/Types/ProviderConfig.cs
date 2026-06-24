namespace PicoNode.AI;

public sealed class ProviderConfig
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public AiApiFormat ApiFormat { get; set; }
    public Dictionary<string, string> ModelMapping { get; set; } = new();
    public int Priority { get; set; }
}
