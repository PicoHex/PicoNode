namespace PicoNode.Agent.Domain;

public interface ILlmClient
{
    /// <summary>
    /// Complete an LLM call given the current configuration, context, and tools.
    /// Returns the final assistant message with all content blocks.
    /// </summary>
    Task<Message> CompleteAsync(
        Llm llm,
        List<Message> context,
        IReadOnlyList<Tool> tools,
        CancellationToken ct
    );
}
