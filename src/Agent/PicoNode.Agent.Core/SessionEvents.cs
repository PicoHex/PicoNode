namespace PicoNode.Agent.Domain;

public sealed record SessionStarted(string Name, List<Participant> Participants) : DomainEvent;
// EntryJson stores pre-serialized JSON to avoid PicoJetson source-generator
// limitation with polymorphic abstract types (SessionTreeEntryBase) nested
// inside DomainEvent-derived types.
public sealed record MessageAppended(string EntryJson) : DomainEvent;
public sealed record LeafMoved(string TargetId) : DomainEvent;
public sealed record SessionRenamed(string NewName) : DomainEvent;
public sealed record SessionDeleted : DomainEvent;
