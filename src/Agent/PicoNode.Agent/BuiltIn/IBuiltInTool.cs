namespace PicoNode.Agent;

public interface IBuiltInTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>
    /// JSON Schema string for native tool calling (Anthropic/OpenAI tools parameter).
    /// Return null to send tool description only via system prompt text.
    /// </summary>
    string? InputSchema { get; }

    /// <returns>(content, isError). Content may be truncated by caller.</returns>
    Task<(string Content, bool IsError)> ExecuteAsync(
        IReadOnlyDictionary<string, object?> args,
        string workingDirectory,
        CancellationToken ct);
}
