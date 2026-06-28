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
