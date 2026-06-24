namespace PicoNode.AI;

public sealed class ContentBlock
{
    public string Type { get; set; } = "text";

    // Text content
    public string? Text { get; set; }

    // Thinking content
    public string? Thinking { get; set; }

    // Image content
    public string? Data { get; set; }
    public string? MimeType { get; set; }

    // Tool call content
    public string? Id { get; set; }
    public string? Name { get; set; }
    public Dictionary<string, object?> Arguments { get; set; } = new();
}
