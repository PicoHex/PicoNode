namespace PicoNode.Agent.Domain;

public interface ILlmClient
{
    /// <summary>
    /// Complete an LLM call. Returns the final assistant message.
    /// For streaming output, use <see cref="StreamAsync"/> instead.
    /// </summary>
    Task<Message> CompleteAsync(
        Llm llm,
        List<Message> context,
        IReadOnlyList<Tool> tools,
        CancellationToken ct
    );

    /// <summary>
    /// Stream LLM output token-by-token. Each event is forwarded to the OutputChannel
    /// in real-time for SSE streaming to the frontend.
    /// </summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(
        Llm llm,
        List<Message> context,
        IReadOnlyList<Tool> tools,
        CancellationToken ct
    );
}
