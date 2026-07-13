# Agent & Session Persistence Design

**Date:** 2026-07-13
**Status:** Approved

## Overview

Currently Agent Actor state (domain events) and Session data (conversation tree) are both in-memory only. This spec defines file-based persistence for both using PicoSerDe polymorphic serialization over JSONL.

## Domain Model

### Agent is an Aggregate Root, Session is a separate Aggregate Root

```
Agent (aggregate)                 Session (aggregate)
├── _llms, _tools, Status         ├── message tree (Id → ParentId)
├── _sessionId: Guid               ├── current leaf position
├── domain event stream            └── Append / MoveTo / BuildContext
└── Session? (runtime reference)

AgentCreated.SessionId links them. One Agent → one Session via ID reference.
Session is independently loadable and survives Agent StopAsync / recreate.
```

All IDs are UUID v7.

### SessionId lifecycle

| Event | What happens |
|-------|-------------|
| CreateAsync | Caller generates UUID v7 → passes to CreateAgent command |
| Mutate(AgentCreated) | `_sessionId = c.SessionId; Session = new Session(c.SessionId)` |
| Post-create | Caller replaces with `new Session(sessionId, new JsonlSessionStorage(sessionId))` |
| GetAsync (rebuild) | `_sessionId` restored from events; caller loads `JsonlSessionStorage(sessionId)` |

## File Layout

```
data/
  actors/{agentId}.jsonl          ← Agent domain events (JSONL, one per line)
  sessions/{sessionId}.jsonl      ← Session tree entries + LeafEntry markers (JSONL)
```

Leaf position: on load, last `LeafEntry` in JSONL determines current leaf. If none, leaf = last entry.
`MoveTo` appends a `LeafEntry`. Normal `AppendEntry` does NOT write state separately.

## Prerequisites

- `PicoJetson.Gen` source generator package (currently only `PicoJetson` runtime is referenced)
- Dependent types must be PicoSerDe-compatible: `Llm`, `Tool`, `Message`, `ContentBlock`, `TokenUsage`

## Component 1: DomainEvent base class

`src/Agent/PicoNode.Agent.Core/DomainEvent.cs` (new)

```csharp
[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[PicoDerivedType(typeof(AgentCreated), "AgentCreated")]
// ... 11 types total
public abstract class DomainEvent : IDomainEvent
```

All existing event records change from `: IDomainEvent` to `: DomainEvent`.

`IEventStore` and `EventSourcedActor._events` remain typed as `IDomainEvent` — framework layer unchanged.

## Component 2: AgentCreated adds SessionId

```csharp
public sealed record CreateAgent(
    List<Llm> Llms, string CurrentProvider, string CurrentModel,
    Guid SessionId, Guid? ParentId = null, List<string>? Packages = null
) : ICommand;

public sealed record AgentCreated(
    List<Llm> Llms, string CurrentProvider, string CurrentModel,
    Guid SessionId, Guid? ParentId, List<string>? Packages
) : DomainEvent;
```

Agent gains:

```csharp
private Guid? _sessionId;
public Guid? SessionId { get; private set; }
public Session? Session { get; set; }  // was private
```

## Component 3: JsonlEventStore

`src/Agent/PicoNode.Agent.Core/JsonlEventStore.cs` (new)

Implements `IEventStore`. ~30 lines.

- `AppendAsync`: check file line count == expectedVersion → serialize each event → append lines → return new version
- `LoadAsync`: `DeserializeAsyncEnumerable<DomainEvent>(stream, topLevelValues: true)` → collect to list
- Concurrency: single actor = serial mailbox = no concurrent writes to same file

Replacement: `new ActorSystem(new JsonlEventStore())`.

## Component 4: SessionTreeEntry + JsonlSessionStorage

`SessionTreeEntryBase` — uncomment the existing `[PicoDerivedType]` declarations (11 types), add `[PicoSerializable]`, add `[PicoPolymorphic]`. Uncomment `[JsonIgnore]` on `object?` properties.

`src/Agent/PicoNode.Agent.Core/JsonlSessionStorage.cs` (new)

Implements `ISessionStorage`. Wraps `InMemorySessionStorage` + file persistence.

- Load: `DeserializeAsyncEnumerable` → feed each line to memory; find last `LeafEntry` for leaf position (fallback: last entry)
- AppendEntry: memory + serialize + append to JSONL
- MoveTo: memory + append `LeafEntry` to JSONL (marks branch switch)
- All queries: memory only

## Component 5: Integration points

| Location | Change |
|----------|--------|
| AgentFactory.BuildAsync | Generate `Guid.CreateVersion7()` for sessionId; inject `JsonlSessionStorage` |
| Server.cs (config save / message endpoint) | Pass sessionId to CreateAgent |
| Agent.RunTurnAsync (SpawnChild) | Generate new sessionId for child agent |
| ActorSystem creation | `new JsonlEventStore()` instead of `InMemoryEventStore` |
| All tests calling CreateAgent | Pass sessionId argument |

## Backward Compatibility

None required. This is a breaking change at the type level (`IDomainEvent` → `DomainEvent`, new required parameter on `CreateAgent`). All call sites receive compile errors and must be updated.

## Testing Strategy

| Layer | Test |
|-------|------|
| `JsonlEventStore` | Round-trip: append events → load → verify count/types match |
| `JsonlSessionStorage` | Round-trip: append messages → load → BuildContext returns correct path |
| `DomainEvent` serialization | Round-trip all 11 event types through PicoSerDe |
| `SessionTreeEntry` serialization | Round-trip all 11 entry types through PicoSerDe |
| Integration | `ActorSystem` + `JsonlEventStore` → CreateAsync → StopAsync → GetAsync → verify state restored |
