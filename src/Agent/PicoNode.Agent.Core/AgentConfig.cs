namespace PicoNode.Agent.Domain;

[PicoSerializable]
[JsonCamelCase]
public sealed class AgentConfig
{
    public Dictionary<string, ProviderEntry> Providers { get; set; } = [];
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public bool ThinkingEnabled { get; set; } = true;
    public int? MaxTokens { get; set; }
    public List<string>? Packages { get; set; }

    public const ThinkingLevel DefaultThinkingLevel = PicoNode.AI.ThinkingLevel.XHigh;

    public static ThinkingLevel? ParseLevel(string? s) =>
        s?.ToLower() switch
        {
            "minimal" => PicoNode.AI.ThinkingLevel.Minimal,
            "low" => PicoNode.AI.ThinkingLevel.Low,
            "medium" => PicoNode.AI.ThinkingLevel.Medium,
            "high" => PicoNode.AI.ThinkingLevel.High,
            "xhigh" => PicoNode.AI.ThinkingLevel.XHigh,
            _ => null,
        };
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ProviderEntry
{
    public string ApiKey { get; set; } = string.Empty;
    public string? ApiFormat { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, string>? Thinking { get; set; }
    public Dictionary<string, ModelThinkingOverride>? Models { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
public sealed class ModelThinkingOverride
{
    public bool ThinkingEnabled { get; set; }
    public string ThinkingLevel { get; set; } = string.Empty;
}
