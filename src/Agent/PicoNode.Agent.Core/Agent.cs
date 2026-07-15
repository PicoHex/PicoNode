namespace PicoNode.Agent.Domain;

/// <summary>
/// Agent aggregate root. Inherits EventSourcedActor — state is managed via
/// domain events (RaiseEvent), persisted by the Actor framework, and
/// restored via ReplayEvents.
/// <para>
/// Agent holds identity (Name), configuration (Llms, Tools, Skills),
/// and self-learned knowledge (Knowledge, SystemPrompt).
/// The turn loop lives in RuntimeActor — Agent is pure identity + config.
/// See docs/superpowers/specs/2026-07-14-agent-session-runtime-redesign.md.
/// </para>
/// </summary>
public sealed class Agent : EventSourcedActor
{
    private readonly List<Llm> _llms = [];
    private readonly List<Tool> _tools = [];
    private readonly List<Guid> _childIds = [];
    private string _name = "";
    private List<SkillInfo> _skills = [];
    private List<string> _knowledge = [];
    private string? _systemPrompt;

    public Llm CurrentLlm { get; private set; } = null!;
    public IReadOnlyList<Llm> LlmsSnapshot => _llms;
    public IReadOnlyList<Tool> ToolsSnapshot => _tools;
    public AgentStatus Status { get; private set; }
    public Guid? ParentId { get; private set; }
    public IReadOnlyList<Guid> ChildIds => _childIds;
    public List<string>? Packages { get; private set; }
    public string? FailureReason { get; private set; }

    // ── Constructors ──

    public Agent(CreateAgent cmd)
        : base(cmd) { }

    public Agent() { }

    // ── Command dispatch ──

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
    {
        switch (command)
        {
            case CreateAgent c:
                ValidateCreate(c);
                RaiseEvent(
                    new AgentCreated(
                        c.Llms,
                        c.CurrentProvider,
                        c.CurrentModel,
                        c.ParentId,
                        c.Packages,
                        c.Name
                    )
                );
                return default;

            case StartAgent:
                if (Status != AgentStatus.Pending)
                    throw new DomainInvariantException($"Cannot start from {Status}");
                RaiseEvent(new AgentStarted());
                return default;

            case CompleteAgent:
                if (Status != AgentStatus.Running)
                    throw new DomainInvariantException($"Cannot complete from {Status}");
                RaiseEvent(new AgentCompleted());
                return default;

            case FailAgent f:
                if (Status != AgentStatus.Running)
                    throw new DomainInvariantException($"Cannot fail from {Status}");
                RaiseEvent(new AgentFailed(f.Reason));
                return default;

            case SwitchLlmCmd s:
                if (_llms.All(l => l.ProviderName != s.ProviderName || l.ModelId != s.ModelId))
                    throw new DomainInvariantException(
                        $"Llm not found: {s.ProviderName}/{s.ModelId}"
                    );
                RaiseEvent(new LlmSwitched(s.ProviderName, s.ModelId));
                return default;

            case AddLlmCmd a:
                if (string.IsNullOrEmpty(a.Llm.ApiKey))
                    throw new DomainInvariantException("ApiKey required");
                RaiseEvent(new LlmAdded(a.Llm));
                return default;

            case RemoveLlmCmd r:
                if (_llms.Count <= 1)
                    throw new DomainInvariantException("Cannot remove the only Llm");
                var match = _llms.FirstOrDefault(l =>
                    l.ProviderName == r.ProviderName && l.ModelId == r.ModelId
                );
                if (match is null)
                    throw new DomainInvariantException(
                        $"Llm not found: {r.ProviderName}/{r.ModelId}"
                    );
                if (ReferenceEquals(match, CurrentLlm))
                    throw new DomainInvariantException("Cannot remove CurrentLlm; switch first");
                RaiseEvent(new LlmRemoved(r.ProviderName, r.ModelId));
                return default;

            case AddToolCmd a:
                if (_tools.Any(t => t.Name == a.Tool.Name))
                    throw new DomainInvariantException($"Tool already exists: {a.Tool.Name}");
                RaiseEvent(new ToolAdded(a.Tool));
                return default;

            case SetToolDescriptionCmd s:
                RaiseEvent(new ToolDescriptionUpdated(s.Name, s.Description));
                return default;

            case RemoveToolCmd r:
                RaiseEvent(new ToolRemoved(r.Name));
                return default;

            case SpawnChildCmd s:
                return SpawnChildAsync(s);

            case SetThinkingLevelCmd s:
                RaiseEvent(new ThinkingLevelSet(s.Level));
                return default;

            case RenameAgent r:
                RaiseEvent(new AgentRenamed(r.NewName));
                return default;

            case LearnSkill l:
                if (_skills.Any(s => s.Name == l.Skill.Name))
                    throw new DomainInvariantException($"Skill already exists: {l.Skill.Name}");
                RaiseEvent(new SkillLearned(l.Skill));
                return default;

            case AccumulateKnowledge a:
                RaiseEvent(new KnowledgeAccumulated(a.Fact));
                return default;

            case EvolveSystemPrompt e:
                RaiseEvent(new SystemPromptEvolved(e.NewPrompt));
                return default;

            case DeleteAgent:
                RaiseEvent(new AgentDeleted());
                return default;

            case GetConfigQuery:
                return new ValueTask<object?>(
                    new AgentConfigSnapshot(
                        _name,
                        _llms.ToList(),
                        _tools.ToList(),
                        _skills.ToList(),
                        _knowledge.ToList(),
                        _systemPrompt
                    )
                );

            case GetAgentNameQuery:
                return new ValueTask<object?>(_name);

            default:
                throw new DomainInvariantException($"Unknown command: {command.GetType().Name}");
        }
    }

    // ── SpawnChild ──

    private async ValueTask<object?> SpawnChildAsync(SpawnChildCmd s)
    {
        // Fail loudly instead of silently dropping the request. Previously a null
        // System made the whole method a no-op: no child, no event, no exception —
        // the caller believed the spawn succeeded.
        if (this.System is null)
            throw new InvalidOperationException(
                "SpawnChild requires an ActorSystem; this actor has none."
            );

        var child = await this.System.CreateAsync<Agent>(
            new CreateAgent(s.Llms, s.CurrentProvider, s.CurrentModel, ParentId: Id)
        );
        // Propagate the requested tools to the child. Previously the Tools field
        // was carried on the command but never applied, so children spawned empty.
        foreach (var tool in s.Tools)
            this.System.Send(child.Id, new AddToolCmd(tool));
        RaiseEvent(new ChildSpawned(child.Id));
        return default;
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case AgentCreated c:
                _llms.Clear();
                _llms.AddRange(c.Llms);
                CurrentLlm = _llms.First(l =>
                    l.ProviderName == c.CurrentProvider && l.ModelId == c.CurrentModel
                );
                ParentId = c.ParentId;
                Packages = c.Packages;
                Status = AgentStatus.Pending;
                _name = c.Name ?? "Unnamed Agent";
                break;
            case AgentStarted:
                Status = AgentStatus.Running;
                break;
            case AgentCompleted:
                Status = AgentStatus.Completed;
                break;
            case AgentFailed f:
                Status = AgentStatus.Failed;
                FailureReason = f.Reason;
                break;
            case LlmSwitched s:
                CurrentLlm = _llms.Last(l =>
                    l.ProviderName == s.ProviderName && l.ModelId == s.ModelId
                );
                break;
            case LlmAdded a:
                _llms.Add(a.Llm);
                break;
            case LlmRemoved r:
                var idx = _llms.FindIndex(l =>
                    l.ProviderName == r.ProviderName && l.ModelId == r.ModelId
                );
                if (idx >= 0)
                    _llms.RemoveAt(idx);
                break;
            case ToolAdded a:
                _tools.Add(a.Tool);
                break;
            case ToolDescriptionUpdated u:
                var existing = _tools.FindIndex(t => t.Name == u.Name);
                if (existing >= 0)
                    _tools[existing].Description = u.Description;
                break;
            case ToolRemoved r:
                _tools.RemoveAll(t => t.Name == r.Name);
                break;
            case ChildSpawned c:
                _childIds.Add(c.ChildId);
                break;
            case ThinkingLevelSet s:
                CurrentLlm.ThinkingLevel = ParseLevel(s.Level);
                break;
            case AgentRenamed e:
                _name = e.NewName;
                break;
            case SkillLearned e:
                _skills.Add(e.Skill);
                break;
            case KnowledgeAccumulated e:
                _knowledge.Add(e.Fact);
                break;
            case SystemPromptEvolved e:
                _systemPrompt = e.NewPrompt;
                break;
            case AgentDeleted:
                break;
        }
    }

    private static void ValidateCreate(CreateAgent cmd)
    {
        if (cmd.Llms is null || cmd.Llms.Count == 0)
            throw new DomainInvariantException("At least one Llm required");
        if (cmd.Llms.Any(l => string.IsNullOrEmpty(l.ApiKey)))
            throw new DomainInvariantException("All Llms must have ApiKey");
        if (
            !cmd.Llms.Any(l =>
                l.ProviderName == cmd.CurrentProvider && l.ModelId == cmd.CurrentModel
            )
        )
            throw new DomainInvariantException("CurrentLlm not in Llms");
    }

    private static Dictionary<string, object?> ParseSimpleJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json is not { Length: > 2 })
            return [];
        try
        {
            var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
            return PicoElementConverter.ObjectToDict(doc.RootElement);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static ThinkingLevel ParseLevel(string level) =>
        level.ToLower() switch
        {
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            "xhigh" => ThinkingLevel.XHigh,
            _ => ThinkingLevel.XHigh,
        };
}
