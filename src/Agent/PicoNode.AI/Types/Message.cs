namespace PicoNode.AI;

[PicoSerializable]
/// <summary>
/// Single concrete message type for LLM communication. Uses Role as discriminator.
/// In session persistence (JSONL), each line is one of these with the role set accordingly.
/// </summary>
public sealed class Message
{
    public string Role { get; set; } = string.Empty; // user | assistant | toolResult
    public long Timestamp { get; set; }

    // User message fields
    public string Content { get; set; } = string.Empty;

    // Assistant message fields
    public ContentBlock[]? ContentBlocks { get; set; }
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public AiApiFormat Api { get; set; }
    public TokenUsage Usage { get; set; } = new();
    public string StopReason { get; set; } = "stop";
    public string? ErrorMessage { get; set; }

    // Tool result fields
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public bool IsError { get; set; }
}

public sealed class TokenUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
