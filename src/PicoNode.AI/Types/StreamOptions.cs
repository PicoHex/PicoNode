namespace PicoNode.AI;

public enum ThinkingLevel
{
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
}

public sealed class StreamOptions
{
    public string? ApiKey { get; set; }
    public int? MaxTokens { get; set; }
    public float? Temperature { get; set; }
    public ThinkingLevel? Reasoning { get; set; }
    public Dictionary<string, string>? ThinkingLevelMap { get; set; }
}
