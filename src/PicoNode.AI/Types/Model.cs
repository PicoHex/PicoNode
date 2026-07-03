namespace PicoNode.AI;

public sealed class Model
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public AiApiFormat Api { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public bool ThinkingEnabled { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Medium;
    public Dictionary<string, string>? ThinkingLevelMap { get; set; }
    public ModelCost Cost { get; set; } = new();
    public int ContextWindow { get; set; }
    public int MaxTokens { get; set; }
}

public sealed class ModelCost
{
    public decimal Input { get; set; }
    public decimal Output { get; set; }
    public decimal CacheRead { get; set; }
    public decimal CacheWrite { get; set; }
}
