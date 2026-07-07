namespace PicoAgent.Web;

internal sealed class SseDeltaEvent
{
    public string type { get; set; } = "delta";
    public string content { get; set; } = string.Empty;
}

internal sealed class SseThinkingEvent
{
    public string type { get; set; } = "thinking";
    public string content { get; set; } = string.Empty;
}

internal sealed class SseDoneEvent
{
    public string type { get; set; } = "done";
    public string stopReason { get; set; } = string.Empty;
}

internal sealed class SseErrorEvent
{
    public string type { get; set; } = "error";
    public string message { get; set; } = string.Empty;
}

internal sealed class SseToolCallStartEvent
{
    public string type { get; set; } = "tool_call_start";
    public string toolCallId { get; set; } = string.Empty;
    public string toolName { get; set; } = string.Empty;
}

internal sealed class SseToolCallDeltaEvent
{
    public string type { get; set; } = "tool_call_delta";
    public string toolCallId { get; set; } = string.Empty;
    public string content { get; set; } = string.Empty;
}

internal sealed class SseToolCallEndEvent
{
    public string type { get; set; } = "tool_call_end";
    public string toolCallId { get; set; } = string.Empty;
}

internal sealed class SseToolResultEvent
{
    public string type { get; set; } = "tool_result";
    public string toolCallId { get; set; } = string.Empty;
    public string toolName { get; set; } = string.Empty;
    public string content { get; set; } = string.Empty;
    public bool isError { get; set; }
}
