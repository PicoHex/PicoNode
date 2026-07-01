namespace PicoNode.AI;

public readonly record struct LlmStreamEvent(
    string Type, // "done" | "error" | "text_delta" | "thinking_delta"
    // | "tool_call_start" | "tool_call_delta" | "tool_call_end"
    string? Text,
    string? StopReason,
    string? ErrorMessage,
    string? ToolCallId = null,
    string? ToolName = null
);

public interface IAgentLlm
{
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        string? systemPrompt,
        Message[] messages,
        string modelId,
        string? reasoningLevel,
        CancellationToken ct
    );
}
