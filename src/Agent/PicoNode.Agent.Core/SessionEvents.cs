namespace PicoNode.Agent.Domain;

public sealed record SessionStarted(string Name, List<Participant> Participants) : DomainEvent;
public sealed record MessageAppended(SessionTreeEntryBase Entry) : DomainEvent;
public sealed record LeafMoved(string TargetId) : DomainEvent;
public sealed record SessionRenamed(string NewName) : DomainEvent;
public sealed record SessionDeleted : DomainEvent;
