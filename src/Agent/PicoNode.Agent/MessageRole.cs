namespace PicoNode.Agent.Domain;

[PicoSerializable]
public sealed class Sender
{
    public Guid AgentId { get; set; }
    public string AgentName { get; set; } = string.Empty;

    public Sender() { }

    public Sender(Guid agentId, string agentName)
    {
        AgentId = agentId;
        AgentName = agentName;
    }
}

[PicoSerializable]
public sealed class CompactionEntry
{
    public int Tag { get; set; }
    public string Summary { get; set; } = string.Empty;

    public CompactionEntry() { }

    public CompactionEntry(int tag, string summary)
    {
        Tag = tag;
        Summary = summary;
    }
}
