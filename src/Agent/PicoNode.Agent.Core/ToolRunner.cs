namespace PicoNode.Agent.Domain;

public sealed class ToolRunner : IToolRunner
{
    private readonly Dictionary<
        string,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>>
    > _handlers = new();

    public void Add(
        Tool tool,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler
    ) => _handlers[tool.Name] = handler;

    public void Add(
        string name,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler
    ) => _handlers[name] = handler;

    public async Task<string> ExecuteAsync(
        string toolName,
        Dictionary<string, object?> args,
        CancellationToken ct
    )
    {
        if (!_handlers.TryGetValue(toolName, out var handler))
            return $"[Tool not found: {toolName}]";

        try
        {
            return await handler(args, ct);
        }
        catch (Exception ex)
        {
            ExceptionHandler.LogOnly(ex, "ToolRunner.cs");
            ExceptionHandler.LogOnly(ex, $"ToolRunner.{toolName}");
            return $"[Tool {toolName} error: {ex.Message}]";
        }
    }
}
