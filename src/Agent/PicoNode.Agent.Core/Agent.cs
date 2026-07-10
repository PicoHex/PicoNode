using PicoNode.Actor.Abs;

namespace PicoNode.Agent.Domain;

/// <summary>
/// Agent aggregate root. Inherits EventSourcedActor — state is managed via
/// domain events (RaiseEvent), persisted by the Actor framework, and
/// restored via ReplayEvents. Session is data (not state), managed separately.
/// </summary>
public sealed class Agent : EventSourcedActor
{
    private readonly List<Llm> _llms = [];
    private readonly List<Tool> _tools = [];
    private readonly List<Guid> _childIds = [];

    public Llm CurrentLlm { get; private set; } = null!;
    public IReadOnlyList<Llm> LlmsSnapshot => _llms;
    public IReadOnlyList<Tool> ToolsSnapshot => _tools;
    public AgentStatus Status { get; private set; }
    public Guid? ParentId { get; private set; }
    public IReadOnlyList<Guid> ChildIds => _childIds;
    public List<string>? Packages { get; private set; }
    public string HomeDir { get; private set; } = null!;
    public string? FailureReason { get; private set; }

    internal ILlmClient? LlmClient { get; init; }
    internal IToolRunner? ToolRunner { get; init; }
    public Session? Session { get; private set; }

    // ── Constructors ──

    public Agent(CreateAgent cmd)
        : base(cmd) { }

    public Agent(CreateAgent cmd, ILlmClient llmClient, IToolRunner toolRunner)
        : base(cmd)
    {
        LlmClient = llmClient;
        ToolRunner = toolRunner;
    }

    public Agent(ILlmClient llmClient, IToolRunner toolRunner)
        : base()
    {
        LlmClient = llmClient;
        ToolRunner = toolRunner;
    }

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
                        c.HomeDir,
                        c.ParentId,
                        c.Packages
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
                if (match.Equals(CurrentLlm))
                    throw new DomainInvariantException("Cannot remove CurrentLlm; switch first");
                RaiseEvent(new LlmRemoved(r.ProviderName, r.ModelId));
                return default;

            case AddToolCmd a:
                if (_tools.Any(t => t.Name == a.Tool.Name))
                    throw new DomainInvariantException($"Tool already exists: {a.Tool.Name}");
                RaiseEvent(new ToolAdded(a.Tool));
                return default;

            case RemoveToolCmd r:
                RaiseEvent(new ToolRemoved(r.Name));
                return default;

            case SpawnChildCmd s:
                return SpawnChildAsync(s);

            case RunTurn r:
                return RunTurnAsync(r.Message, StopToken);

            case SetThinkingLevelCmd s:
                RaiseEvent(new ThinkingLevelSet(s.Level));
                return default;

            default:
                throw new DomainInvariantException($"Unknown command: {command.GetType().Name}");
        }
    }

    // ── SpawnChild ──

    private async ValueTask<object?> SpawnChildAsync(SpawnChildCmd s)
    {
        if (this.System is not null)
        {
            var child = await this.System.CreateAsync<Agent>(
                new CreateAgent(s.Llms, s.CurrentProvider, s.CurrentModel, HomeDir, Id)
            );
            RaiseEvent(new ChildSpawned(child.Id));
        }
        return default;
    }

    // ── RunTurn ──

    private async ValueTask<object?> RunTurnAsync(string message, CancellationToken ct)
    {
        if (LlmClient is null || ToolRunner is null)
            throw new InvalidOperationException(
                "RunTurn requires LlmClient and ToolRunner — register them via the Agent constructor."
            );

        try
        {
            var session = Session!;

        var userMsg = new Message
        {
            Role = "user",
            Content = message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await session.Append(new MessageEntry { Message = userMsg });

        bool hasTools;
        var iterations = 0;
        do
        {
            if (++iterations > 100)
                break;

            var ctx = await session.BuildContext();
            var response = await LlmClient!.CompleteAsync(CurrentLlm, ctx, ToolsSnapshot, ct);

            if (response.ContentBlocks is not { Length: > 0 })
                response.ContentBlocks =
                [
                    new ContentBlock { Type = "text", Text = "[No response from LLM]" },
                ];

            await session.Append(new MessageEntry { Message = response });

            // Forward all content blocks to progress channel
            if (response.ContentBlocks is { Length: > 0 })
            {
                foreach (var cb in response.ContentBlocks)
                {
                    if (cb.Type is "text" or "thinking" or "reasoning")
                        WriteOutput(cb.Type, cb.Text);
                }
            }

            var toolCalls = response.ContentBlocks?.Where(cb => cb.Type == "tool_call").ToArray();
            hasTools = toolCalls is { Length: > 0 };

            if (hasTools)
            {
                foreach (var tc in toolCalls!)
                {
                    var toolResult = await ToolRunner!.ExecuteAsync(
                        tc.Name ?? "",
                        tc.Arguments,
                        ct
                    );
                    var toolMsg = new Message
                    {
                        Role = "toolResult",
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        ContentBlocks = [new ContentBlock { Type = "text", Text = toolResult }],
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    await session.Append(new MessageEntry { Message = toolMsg });
                    WriteOutput("tool_result", toolResult);
                }
            }
        } while (hasTools && !ct.IsCancellationRequested);

        WriteOutput("done");
        return default;
    }
    catch (Exception ex)
    {
        WriteOutput("error", ex.Message);
        throw;
    }
    finally
    {
        OutputWriter?.Complete();
    }
    }

    // ── State recovery ──

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case AgentCreated c:
                _llms.Clear();
                _llms.AddRange(c.Llms);
                CurrentLlm = c.Llms.First(l =>
                    l.ProviderName == c.CurrentProvider && l.ModelId == c.CurrentModel
                );
                HomeDir = c.HomeDir;
                ParentId = c.ParentId;
                Packages = c.Packages;
                Status = AgentStatus.Pending;
                Session = new Session(Guid.CreateVersion7(), new InMemorySessionStorage());
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
                CurrentLlm = _llms.First(l =>
                    l.ProviderName == s.ProviderName && l.ModelId == s.ModelId
                );
                break;
            case LlmAdded a:
                _llms.Add(a.Llm);
                break;
            case LlmRemoved r:
                _llms.RemoveAll(l => l.ProviderName == r.ProviderName && l.ModelId == r.ModelId);
                break;
            case ToolAdded a:
                _tools.Add(a.Tool);
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
