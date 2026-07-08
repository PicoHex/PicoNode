namespace PicoNode.Agent.Domain;

public interface IToolRunner
{
    /// <summary>
    /// Execute a tool by name and return the result text.
    /// </summary>
    Task<string> ExecuteAsync(
        string toolName,
        Dictionary<string, object?> args,
        CancellationToken ct
    );
}
