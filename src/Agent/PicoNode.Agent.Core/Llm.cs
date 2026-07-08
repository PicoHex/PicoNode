namespace PicoNode.Agent.Domain;

public sealed class Llm : IEquatable<Llm>
{
    public string ProviderName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public AiApiFormat ApiFormat { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; }
    public int MaxTokens { get; set; }
    public bool ThinkingEnabled { get; set; }

    public bool Equals(Llm? other) =>
        other is not null && ProviderName == other.ProviderName && ModelId == other.ModelId;

    public override bool Equals(object? obj) => Equals(obj as Llm);

    public override int GetHashCode() => HashCode.Combine(ProviderName, ModelId);
}
