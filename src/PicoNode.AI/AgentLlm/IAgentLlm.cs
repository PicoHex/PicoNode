namespace PicoNode.AI;

public readonly record struct LlmStreamEvent(
    string Type,        // "done" | "error" | "text_delta" | "thinking_delta"
    string? Text,
    string? StopReason,
    string? ErrorMessage
);

public interface IAgentLlm
{
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        string? systemPrompt,
        Message[] messages,
        string modelId,
        string? reasoningLevel,
        CancellationToken ct);
}
