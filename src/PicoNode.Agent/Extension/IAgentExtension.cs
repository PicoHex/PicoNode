namespace PicoNode.Agent;

public interface IAgentExtension
{
    Task<bool> OnToolCallAsync(string toolName, byte[] args, CancellationToken ct);
    Task<string?> OnSystemPromptAsync(string current);
    Task<byte[]?> OnToolResultAsync(string toolName, byte[] result, CancellationToken ct);
}
