namespace PicoAgent;

/// <summary>
/// SSE event DTOs for deserializing incoming events from the Agent backend.
/// Mirror the JSON shape: {"type":"delta","content":"Hi"}.
/// </summary>
[PicoSerializable]
[JsonCamelCase]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "type")]
[PicoDerivedType(typeof(SseInDelta), "delta")]
[PicoDerivedType(typeof(SseInThinking), "thinking")]
[PicoDerivedType(typeof(SseInToolCallStart), "tool_call_start")]
[PicoDerivedType(typeof(SseInToolCallDelta), "tool_call_delta")]
[PicoDerivedType(typeof(SseInToolCallEnd), "tool_call_end")]
[PicoDerivedType(typeof(SseInToolResult), "tool_result")]
[PicoDerivedType(typeof(SseInDone), "done")]
[PicoDerivedType(typeof(SseInError), "error")]
class SseInEvent { }

[PicoSerializable]
[JsonCamelCase]
sealed class SseInDelta : SseInEvent
{
    public string Content { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
sealed class SseInThinking : SseInEvent
{
    public string Content { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
sealed class SseInToolCallStart : SseInEvent
{
    public string ToolCallId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
sealed class SseInToolCallDelta : SseInEvent
{
    public string ToolCallId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
sealed class SseInToolCallEnd : SseInEvent
{
    public string ToolCallId { get; set; } = string.Empty;
}

[PicoSerializable]
[JsonCamelCase]
sealed class SseInToolResult : SseInEvent
{
    public string ToolCallId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsError { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
sealed class SseInDone : SseInEvent
{
    public string? StopReason { get; set; }
}

[PicoSerializable]
[JsonCamelCase]
sealed class SseInError : SseInEvent
{
    public string? Message { get; set; }
}
