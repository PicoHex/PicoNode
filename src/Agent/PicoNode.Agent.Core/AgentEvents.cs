namespace PicoNode.Agent.Domain;

public sealed record AgentCreated(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    Guid SessionId,
    Guid? ParentId,
    List<string>? Packages
) : DomainEvent;

public sealed record AgentStarted : DomainEvent;

public sealed record AgentCompleted : DomainEvent;

public sealed record AgentFailed(string Reason) : DomainEvent;

public sealed record LlmSwitched(string ProviderName, string ModelId) : DomainEvent;

public sealed record LlmAdded(Llm Llm) : DomainEvent;

public sealed record LlmRemoved(string ProviderName, string ModelId) : DomainEvent;

public sealed record ToolAdded(Tool Tool) : DomainEvent;

public sealed record ToolRemoved(string Name) : DomainEvent;

public sealed record ChildSpawned(Guid ChildId) : DomainEvent;

public sealed record ThinkingLevelSet(string Level) : DomainEvent;
