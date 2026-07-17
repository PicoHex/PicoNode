namespace PicoNode.Agent.Domain;

public sealed record CreateAgent(string Name, Guid LlmId) : ICommand;

public sealed record SetLlmCmd(Guid LlmId) : ICommand;

public sealed record SetThinkingLevelCmd(string Level) : ICommand;

public sealed record SetMaxTokensCmd(int MaxTokens) : ICommand;

public sealed record SetThinkingEnabledCmd(bool Enabled) : ICommand;

public sealed record AddToolCmd(Tool Tool) : ICommand;

public sealed record SetToolDescriptionCmd(string Name, string Description) : ICommand;

public sealed record RemoveToolCmd(string Name) : ICommand;

public sealed record RenameAgent(string NewName) : ICommand;

public sealed record LearnSkill(SkillInfo Skill) : ICommand;

public sealed record AccumulateKnowledge(string Fact) : ICommand;

public sealed record EvolveSystemPrompt(string NewPrompt) : ICommand;

public sealed record DeleteAgent : ICommand;

public sealed record GetConfigQuery : ICommand;

public sealed record GetAgentNameQuery : ICommand;

[PicoSerializable]
public sealed class AgentConfigSnapshot
{
    public string Name { get; set; } = string.Empty;
    public Guid LlmId { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; }
    public int MaxTokens { get; set; }
    public bool ThinkingEnabled { get; set; }
    public List<Tool> Tools { get; set; } = [];
    public List<SkillInfo> Skills { get; set; } = [];
    public List<string> Knowledge { get; set; } = [];
    public string? SystemPrompt { get; set; }

    public AgentConfigSnapshot() { }

    public AgentConfigSnapshot(
        string name, Guid llmId, ThinkingLevel thinkingLevel,
        int maxTokens, bool thinkingEnabled,
        List<Tool> tools, List<SkillInfo> skills,
        List<string> knowledge, string? systemPrompt)
    {
        Name = name; LlmId = llmId; ThinkingLevel = thinkingLevel;
        MaxTokens = maxTokens; ThinkingEnabled = thinkingEnabled;
        Tools = tools; Skills = skills;
        Knowledge = knowledge; SystemPrompt = systemPrompt;
    }
}
