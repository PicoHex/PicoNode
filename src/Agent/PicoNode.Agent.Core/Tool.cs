namespace PicoNode.Agent.Domain;

public sealed class Tool : IEquatable<Tool>
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? InputSchema { get; set; }
    public ToolKind Kind { get; set; }
    public string? Handler { get; set; }

    public bool Equals(Tool? other) => other is not null && Name == other.Name;

    public override bool Equals(object? obj) => Equals(obj as Tool);

    public override int GetHashCode() => Name.GetHashCode();
}
