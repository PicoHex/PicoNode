namespace PicoAgent.Web;

internal sealed class SseDeltaEvent
{
    public string type { get; set; } = "delta";
    public string content { get; set; } = "";
}

internal sealed class SseThinkingEvent
{
    public string type { get; set; } = "thinking";
    public string content { get; set; } = "";
}

internal sealed class SseDoneEvent
{
    public string type { get; set; } = "done";
    public string stopReason { get; set; } = "";
}

internal sealed class SseErrorEvent
{
    public string type { get; set; } = "error";
    public string message { get; set; } = "";
}

internal sealed class SseToolCallStartEvent
{
    public string type { get; set; } = "tool_call_start";
    public string toolCallId { get; set; } = "";
    public string toolName { get; set; } = "";
}

internal sealed class SseToolCallDeltaEvent
{
    public string type { get; set; } = "tool_call_delta";
    public string toolCallId { get; set; } = "";
    public string content { get; set; } = "";
}

internal sealed class SseToolCallEndEvent
{
    public string type { get; set; } = "tool_call_end";
    public string toolCallId { get; set; } = "";
}
