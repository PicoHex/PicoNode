namespace PicoNode.Agent.Domain;

public sealed record AgentCreated(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    Guid? ParentId,
    List<string>? Packages,
    string? Name = null
) : DomainEvent;

public sealed record AgentStarted : DomainEvent;

public sealed record AgentCompleted : DomainEvent;

public sealed record AgentFailed(string Reason) : DomainEvent;

public sealed record LlmSwitched(string ProviderName, string ModelId) : DomainEvent;

public sealed record LlmAdded(Llm Llm) : DomainEvent;

public sealed record LlmRemoved(string ProviderName, string ModelId) : DomainEvent;

public sealed record ToolAdded(Tool Tool) : DomainEvent;

public sealed record ToolDescriptionUpdated(string Name, string Description) : DomainEvent;

public sealed record ToolRemoved(string Name) : DomainEvent;

public sealed record ChildSpawned(Guid ChildId) : DomainEvent;

public sealed record ThinkingLevelSet(string Level) : DomainEvent;

public sealed record AgentRenamed(string NewName) : DomainEvent;

public sealed record SkillLearned(SkillInfo Skill) : DomainEvent;

public sealed record SkillRefined(string Name, SkillInfo Delta) : DomainEvent;

public sealed record KnowledgeAccumulated(string Fact) : DomainEvent;

public sealed record SystemPromptEvolved(string NewPrompt) : DomainEvent;

public sealed record AgentDeleted : DomainEvent;
