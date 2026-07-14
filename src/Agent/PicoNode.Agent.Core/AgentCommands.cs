namespace PicoNode.Agent.Domain;

public sealed record CreateAgent(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    Guid? ParentId = null,
    List<string>? Packages = null,
    string? Name = null
) : ICommand;

public sealed record StartAgent : ICommand;

public sealed record CompleteAgent : ICommand;

public sealed record FailAgent(string Reason) : ICommand;

public sealed record SwitchLlmCmd(string ProviderName, string ModelId) : ICommand;

public sealed record AddLlmCmd(Llm Llm) : ICommand;

public sealed record RemoveLlmCmd(string ProviderName, string ModelId) : ICommand;

public sealed record AddToolCmd(Tool Tool) : ICommand;

public sealed record RemoveToolCmd(string Name) : ICommand;

public sealed record SpawnChildCmd(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    List<Tool> Tools
) : ICommand;

public sealed record SetThinkingLevelCmd(string Level) : ICommand;

public sealed record RenameAgent(string NewName) : ICommand;
public sealed record LearnSkill(SkillInfo Skill) : ICommand;
public sealed record AccumulateKnowledge(string Fact) : ICommand;
public sealed record EvolveSystemPrompt(string NewPrompt) : ICommand;
public sealed record DeleteAgent : ICommand;
public sealed record GetConfigQuery : ICommand;
public sealed record GetAgentNameQuery : ICommand;

public sealed record AgentConfigSnapshot(
    string Name,
    List<Llm> Llms,
    List<Tool> Tools,
    List<SkillInfo> Skills,
    List<string> Knowledge,
    string? SystemPrompt
);
