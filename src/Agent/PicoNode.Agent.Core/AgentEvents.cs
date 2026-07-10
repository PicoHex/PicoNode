namespace PicoNode.Agent.Domain;

public sealed record AgentCreated(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    string HomeDir,
    Guid? ParentId,
    List<string>? Packages
) : IDomainEvent;

public sealed record AgentStarted : IDomainEvent;

public sealed record AgentCompleted : IDomainEvent;

public sealed record AgentFailed(string Reason) : IDomainEvent;

public sealed record LlmSwitched(string ProviderName, string ModelId) : IDomainEvent;

public sealed record LlmAdded(Llm Llm) : IDomainEvent;

public sealed record LlmRemoved(string ProviderName, string ModelId) : IDomainEvent;

public sealed record ToolAdded(Tool Tool) : IDomainEvent;

public sealed record ToolRemoved(string Name) : IDomainEvent;

public sealed record ChildSpawned(Guid ChildId) : IDomainEvent;

public sealed record ThinkingLevelSet(string Level) : IDomainEvent;
