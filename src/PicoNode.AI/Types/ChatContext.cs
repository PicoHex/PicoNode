namespace PicoNode.AI;

public sealed class ChatContext
{
    public string? SystemPrompt { get; set; }
    public Message[] Messages { get; set; } = [];
    public ToolSchema[]? Tools { get; set; }
}

public sealed class ToolSchema
{
    public string Type { get; set; } = "function";
    public ToolSchemaFunction Function { get; set; } = new();
}

public sealed class ToolSchemaFunction
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Parameters { get; set; } = "{}";
}
