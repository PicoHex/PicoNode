namespace PicoNode.Agent.Domain;

[PicoSerializable]
public sealed class SessionContext
{
    public List<Message> Messages { get; set; } = [];

    public SessionContext() { }

    public SessionContext(List<Message> messages, object? _ = null)
    {
        Messages = messages;
    }
}
