namespace PicoNode.Agent.Domain;

public sealed record AgentCreated(string Name, Guid LlmId) : DomainEvent;

public sealed record LlmChanged(Guid LlmId) : DomainEvent;

public sealed record ThinkingLevelSet(string Level) : DomainEvent;

public sealed record MaxTokensSet(int MaxTokens) : DomainEvent;

public sealed record ThinkingEnabledSet(bool Enabled) : DomainEvent;

public sealed record ToolAdded(Tool Tool) : DomainEvent;

public sealed record ToolDescriptionUpdated(string Name, string Description) : DomainEvent;

public sealed record ToolRemoved(string Name) : DomainEvent;

public sealed record AgentRenamed(string NewName) : DomainEvent;

public sealed record SkillLearned(SkillInfo Skill) : DomainEvent;

public sealed record KnowledgeAccumulated(string Fact) : DomainEvent;

public sealed record SystemPromptEvolved(string NewPrompt) : DomainEvent;

public sealed record AgentDeleted : DomainEvent;
