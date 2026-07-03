namespace PicoAgent;

public sealed class AgentResult
{
    public required string Text { get; init; }
    public required IReadOnlyList<Message> NewMessages { get; init; }
    public string? StopReason { get; init; }
    public TokenUsage? Usage { get; init; }

    [SetsRequiredMembers]
    public AgentResult(string text, IReadOnlyList<Message> newMessages)
    {
        Text = text;
        NewMessages = newMessages;
    }
}
