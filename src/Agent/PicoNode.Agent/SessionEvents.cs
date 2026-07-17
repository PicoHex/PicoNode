namespace PicoNode.Agent.Domain;

public sealed record SessionStarted(string Name) : DomainEvent;

public sealed record MessageAppended(Message Message) : DomainEvent;

public sealed record CompactionExecuted(int Tag, string Summary) : DomainEvent;

public sealed record SessionRenamed(string NewName) : DomainEvent;

public sealed record SessionDeleted : DomainEvent;
