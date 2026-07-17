namespace PicoNode.AI;

public enum MessageRole : byte
{
    User = 0,
    Assistant = 1,
    ToolResult = 2,
    System = 3
}

public static class MessageRoleExtensions
{
    public static string ToRoleString(this MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.ToolResult => "toolResult",
        MessageRole.System => "system",
        _ => "user"
    };

    public static MessageRole ParseRole(string role) => role switch
    {
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        "toolResult" => MessageRole.ToolResult,
        "system" => MessageRole.System,
        _ => MessageRole.User
    };
}
