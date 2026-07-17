namespace PicoNode.Agent.Domain;

public sealed class Agent : EventSourcedActor
{
    private string _name = "";
    private Guid _llmId;
    private ThinkingLevel _thinkingLevel;
    private int _maxTokens = 4096;
    private bool _thinkingEnabled = true;
    private readonly List<Tool> _tools = [];
    private readonly List<SkillInfo> _skills = [];
    private readonly List<string> _knowledge = [];
    private string? _systemPrompt;

    public Agent(CreateAgent cmd) : base(cmd) { }
    public Agent() { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
    {
        switch (command)
        {
            case CreateAgent c:
                ValidateCreate(c);
                RaiseEvent(new AgentCreated(c.Name, c.LlmId));
                return default;

            case SetLlmCmd c:
                RaiseEvent(new LlmChanged(c.LlmId));
                return default;

            case SetThinkingLevelCmd c:
                RaiseEvent(new ThinkingLevelSet(c.Level));
                return default;

            case SetMaxTokensCmd c:
                if (c.MaxTokens <= 0)
                    throw new DomainInvariantException("MaxTokens must be positive");
                RaiseEvent(new MaxTokensSet(c.MaxTokens));
                return default;

            case SetThinkingEnabledCmd c:
                RaiseEvent(new ThinkingEnabledSet(c.Enabled));
                return default;

            case AddToolCmd c:
                ValidateAddTool(c.Tool);
                RaiseEvent(new ToolAdded(c.Tool));
                return default;

            case SetToolDescriptionCmd c:
                RaiseEvent(new ToolDescriptionUpdated(c.Name, c.Description));
                return default;

            case RemoveToolCmd c:
                RaiseEvent(new ToolRemoved(c.Name));
                return default;

            case RenameAgent c:
                RaiseEvent(new AgentRenamed(c.NewName));
                return default;

            case LearnSkill c:
                RaiseEvent(new SkillLearned(c.Skill));
                return default;

            case AccumulateKnowledge c:
                RaiseEvent(new KnowledgeAccumulated(c.Fact));
                return default;

            case EvolveSystemPrompt c:
                RaiseEvent(new SystemPromptEvolved(c.NewPrompt));
                return default;

            case DeleteAgent:
                RaiseEvent(new AgentDeleted());
                return default;

            case GetConfigQuery:
                return new ValueTask<object?>(new AgentConfigSnapshot(
                    _name, _llmId, _thinkingLevel, _maxTokens,
                    _thinkingEnabled, new List<Tool>(_tools),
                    new List<SkillInfo>(_skills),
                    new List<string>(_knowledge), _systemPrompt));

            case GetAgentNameQuery:
                return new ValueTask<object?>(_name);

            default:
                throw new DomainInvariantException($"Unknown command: {command.GetType().Name}");
        }
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case AgentCreated e:
                _name = e.Name; _llmId = e.LlmId; break;
            case LlmChanged e:
                _llmId = e.LlmId; break;
            case ThinkingLevelSet e:
                _thinkingLevel = ParseLevel(e.Level); break;
            case MaxTokensSet e:
                _maxTokens = e.MaxTokens; break;
            case ThinkingEnabledSet e:
                _thinkingEnabled = e.Enabled; break;
            case ToolAdded e:
                _tools.Add(e.Tool); break;
            case ToolDescriptionUpdated e:
                var idx = _tools.FindIndex(t => t.Name == e.Name);
                if (idx >= 0) _tools[idx].Description = e.Description;
                break;
            case ToolRemoved e:
                _tools.RemoveAll(t => t.Name == e.Name); break;
            case AgentRenamed e:
                _name = e.NewName; break;
            case SkillLearned e:
                _skills.Add(e.Skill); break;
            case KnowledgeAccumulated e:
                _knowledge.Add(e.Fact); break;
            case SystemPromptEvolved e:
                _systemPrompt = e.NewPrompt; break;
            case AgentDeleted: break;
        }
    }

    private static void ValidateCreate(CreateAgent cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            throw new DomainInvariantException("Agent name is required");
        if (cmd.LlmId == Guid.Empty)
            throw new DomainInvariantException("LlmId is required");
    }

    private static void ValidateAddTool(Tool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name))
            throw new DomainInvariantException("Tool name is required");
    }

    private static ThinkingLevel ParseLevel(string level) =>
        level switch
        {
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            "xhigh" => ThinkingLevel.XHigh,
            _ => throw new DomainInvariantException($"Unknown ThinkingLevel: '{level}'"),
        };
}
