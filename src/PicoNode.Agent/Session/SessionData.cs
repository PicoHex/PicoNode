namespace PicoNode.Agent;

public sealed class SessionData
{
    public List<Message> Messages { get; set; } = [];
    public bool ThinkingEnabled { get; set; } = true;
    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Medium;
}
