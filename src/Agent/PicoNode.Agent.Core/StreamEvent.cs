namespace PicoNode.Agent.Domain;

/// <summary>
/// A single streaming event from the LLM, forwarded to OutputChannel in real-time.
/// </summary>
public sealed class StreamEvent
{
    public string Type { get; init; } = null!;
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
}
