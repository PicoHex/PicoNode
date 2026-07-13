# Agent & Session Persistence — Implementation Plan

**Date:** 2026-07-13
**Spec:** `docs/superpowers/specs/2026-07-13-agent-session-persistence.md`

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `Directory.Packages.props` | Modify | Add PicoJetson.Gen, PicoSerDe.Core |
| `PicoNode.Agent.Core.csproj` | Modify | Add package refs |
| `DomainEvent.cs` | **New** | Abstract base with PicoDerivedType × 11 |
| `AgentEvents.cs` | Modify | `: IDomainEvent` → `: DomainEvent`; AgentCreated +SessionId |
| `AgentCommands.cs` | Modify | CreateAgent +SessionId |
| `Agent.cs` | Modify | _sessionId; SessionId/Session props; Mutate updates |
| `JsonlEventStore.cs` | **New** | IEventStore file-backed implementation |
| `JsonlSessionStorage.cs` | **New** | ISessionStorage file-backed implementation |
| `SessionTreeEntry.cs` | Modify | Uncomment PicoDerivedType, JsonIgnore |
| `AgentFactory.cs` | Modify | Generate sessionId; inject storage |
| `Server.cs` | Modify | Pass sessionId |
| `JsonlEventStoreTests.cs` | **New** | Round-trip + concurrency tests |
| `JsonlSessionStorageTests.cs` | **New** | Round-trip + MoveTo tests |
| `DomainEventSerializationTests.cs` | **New** | All 11 types round-trip |
| Existing tests (*.cs) | Modify | Add sessionId to CreateAgent calls |

---

## Tasks

### Phase 0: Prerequisites

#### Task 0.1 — Add PicoJetson.Gen + PicoSerDe.Core packages
- Add `<PackageVersion Include="PicoJetson.Gen" Version="..."/>` to `Directory.Packages.props`
- Check PicoSerDe.Core — if separate, add it too; if transitive from PicoJetson, skip
- Add `<PackageReference Include="PicoJetson.Gen"/>` to `PicoNode.Agent.Core.csproj`
- `dotnet build` — verify source generator activates (no errors)
- Commit: `build: add PicoJetson.Gen for polymorphic JSONL serialization`

---

### Phase 1: DomainEvent Base Class

#### Task 1.1 — Write DomainEvent class
- Create `src/Agent/PicoNode.Agent.Core/DomainEvent.cs`
- `[PicoSerializable]`, `[PicoPolymorphic(TypeDiscriminatorPropertyName = "$type")]`
- `[PicoDerivedType]` for all 11 event types: AgentCreated, AgentStarted, AgentCompleted, AgentFailed, LlmSwitched, LlmAdded, LlmRemoved, ToolAdded, ToolRemoved, ChildSpawned, ThinkingLevelSet
- `public abstract class DomainEvent : IDomainEvent`
- `dotnet build` — must pass
- Commit

#### Task 1.2 — Migrate event records to DomainEvent
- In `AgentEvents.cs`: change all `: IDomainEvent` to `: DomainEvent`
- In `AgentCommands.cs`: no change (commands don't inherit from DomainEvent)
- `dotnet build` — must pass
- Commit: `refactor: migrate domain events to DomainEvent base class`

#### Task 1.3 — Write DomainEvent serialization round-trip test (TDD: RED)
- Create `tests/PicoNode.Agent.Core.Tests/DomainEventSerializationTests.cs`
- One `[Test]` per event type (11 tests), plus one composite test
- Each test: create event instance → `JsonSerializer.Serialize<DomainEvent>` → verify JSON contains `"$type":"..."` → `Deserialize<DomainEvent>` → verify type + all fields match
- Run → 11 tests fail (DomainEvent type not yet known to PicoSerDe)

#### Task 1.4 — Fix round-trip (TDD: GREEN)
- If any test fails due to missing type registration, add `[GenerateSerializer]` or `[PicoSerializable]` on the missing type (Llm, Tool, etc.)
- Run → all 11 pass
- Commit: `test: add domain event serialization round-trip`

---

### Phase 2: AgentCreated SessionId

#### Task 2.1 — Add SessionId to CreateAgent command and AgentCreated event (TDD: RED)
- Write test: `CreateAgent_WithSessionId_RaisesAgentCreatedWithSameId`
- Run → compile error (SessionId doesn't exist yet)
- Add `Guid SessionId` to `CreateAgent` and `AgentCreated` records
- Run → compile error persists on call sites

#### Task 2.2 — Update all CreateAgent call sites
- `AgentFactory.cs`: `var sessionId = Guid.CreateVersion7();`
- `Server.cs`: generate sessionId, pass to CreateAgent
- `Agent.cs` RunTurnAsync (SpawnChild): generate sessionId
- All test files: add `Guid.CreateVersion7()` as sessionId argument
- `dotnet build` → must pass

#### Task 2.3 — Add SessionId to Agent state + Mutate (TDD: GREEN)
- Agent: add `private Guid? _sessionId; public Guid? SessionId { get; private set; }`
- Agent: change `Session` setter from `private` to `public`
- `Mutate(AgentCreated)`: `SessionId = c.SessionId; Session = new Session(c.SessionId);`
- Run test → pass
- Commit: `feat: add SessionId to Agent aggregate`

---

### Phase 3: JsonlEventStore

#### Task 3.1 — Write JsonlEventStore append + load test (TDD: RED)
- Create `tests/PicoNode.Agent.Core.Tests/JsonlEventStoreTests.cs`
- Test: `AppendThenLoad_RoundTrips`: create store → append 3 events → load → verify 3 events, correct types
- Test: `Concurrency_Throws`: append v0 → append v0 again → throws ConcurrencyException
- Test: `Load_EmptyFile_ReturnsEmpty`: load non-existent actor → empty list
- Run → 3 compile errors (JsonlEventStore doesn't exist)

#### Task 3.2 — Implement JsonlEventStore (TDD: GREEN)
- Create `src/Agent/PicoNode.Agent.Core/JsonlEventStore.cs`
- Constructor: `string baseDir = "data/actors"`, `Directory.CreateDirectory`
- `AppendAsync`: check line count → serialize → append → return new version
- `LoadAsync`: `DeserializeAsyncEnumerable<DomainEvent>` → collect
- Run → 3 tests pass
- `dotnet test` all → no regressions
- Commit: `feat: add JsonlEventStore for file-backed actor event persistence`

#### Task 3.3 — Cleanup test files after test
- `JsonlEventStore` tests should write to a temp directory
- `[Before(Test)]` or constructor creates unique temp dir; `[After(Test)]` deletes it

---

### Phase 4: Session Storage

#### Task 4.1 — Uncomment SessionTreeEntry attributes
- `SessionTreeEntry.cs`: uncomment `[PicoSerializable]`, `[PicoPolymorphic]`, all `[PicoDerivedType]`, all `[JsonIgnore]` on `object?` properties
- `dotnet build` → must pass
- Commit: `feat: enable PicoSerDe polymorphic serialization for session entries`

#### Task 4.2 — Write JsonlSessionStorage tests (TDD: RED)
- Create `tests/PicoNode.Agent.Core.Tests/JsonlSessionStorageTests.cs`
- Test: `AppendMessage_RoundTrips`: append MessageEntry → load new instance → BuildContext returns message
- Test: `MoveTo_Persists`: append A, append B, MoveTo(A) → load → leaf = A
- Test: `EmptySession_ReturnsEmpty`: load non-existent session → no entries
- Run → compile errors

#### Task 4.3 — Implement JsonlSessionStorage (TDD: GREEN)
- Create `src/Agent/PicoNode.Agent.Core/JsonlSessionStorage.cs`
- Constructor: load from file if exists → feed to InMemorySessionStorage
- `AppendEntry`: memory + serialize + append to file
- `MoveTo`: memory + append LeafEntry to file
- `GetLeafId`: from memory (restored on load from last LeafEntry or last entry)
- All other methods: delegate to memory
- Run → 3 tests pass
- Commit: `feat: add JsonlSessionStorage for file-backed session persistence`

---

### Phase 5: Integration + Wiring

#### Task 5.1 — Wire JsonlEventStore into ActorSystem creation
- Find all `new ActorSystem(new InMemoryEventStore())` call sites
- Replace with `new ActorSystem(new JsonlEventStore())`
- For tests: keep InMemoryEventStore (tests shouldn't touch filesystem)
- Commit

#### Task 5.2 — Wire JsonlSessionStorage into Agent creation
- `AgentFactory.BuildAsync`: after CreateAsync, replace Session with `new Session(sessionId, new JsonlSessionStorage(sessionId))`
- `Server.cs`: same replacement
- Commit

#### Task 5.3 — Write integration test
- `Integration_StopAndRebuild_RestoresState`: create Agent with JsonlEventStore → add tool → stop → GetAsync → verify tool is in ToolsSnapshot
- Run → pass
- Commit: `test: add integration test for agent state rebuild`

#### Task 5.4 — Final regression run
- `dotnet test` all test projects → zero failures
- Manual smoke test: start server, send message, restart, verify history loaded
- Commit any remaining fixes
