namespace PicoNode.Agent;

[PicoSerializable]
[PicoJetson.JsonCamelCase]
public sealed class AgentConfig
{
    public Dictionary<string, ProviderEntry> Providers { get; set; } = [];
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public bool ThinkingEnabled { get; set; } = true;
    public int? MaxTokens { get; set; }

    /// <summary>Default thinking level when none is configured.</summary>
    public const ThinkingLevel DefaultThinkingLevel = PicoNode.AI.ThinkingLevel.XHigh;
    public static string DefaultThinkingLevelString => DefaultThinkingLevel.ToString().ToLowerInvariant();

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

    public static void ApplyModelOverride(Model model, ModelThinkingOverride? modelOverride)
    {
        if (modelOverride is null)
            return;
        model.ThinkingEnabled = modelOverride.ThinkingEnabled;
        if (modelOverride.ThinkingLevel is { Length: > 0 } tl)
            model.ThinkingLevel = ParseLevel(tl) ?? model.ThinkingLevel;
    }
}

[PicoSerializable]
[PicoJetson.JsonCamelCase]
public sealed class ProviderEntry
{
    public string ApiKey { get; set; } = string.Empty;
    public string? ApiFormat { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, string>? Thinking { get; set; }
    public Dictionary<string, ModelThinkingOverride>? Models { get; set; }
}

[PicoSerializable]
[PicoJetson.JsonCamelCase]
public sealed class ModelThinkingOverride
{
    public bool ThinkingEnabled { get; set; }
    public string ThinkingLevel { get; set; } = string.Empty;
}
