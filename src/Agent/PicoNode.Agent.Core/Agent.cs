namespace PicoNode.Agent.Domain;

/// <summary>
/// Agent aggregate root. Inherits EventSourcedActor — state is managed via
/// domain events (RaiseEvent), persisted by the Actor framework, and
/// restored via ReplayEvents. Session is data (not state), managed separately.
/// <para>
/// Turn loop is mailbox-driven: RunTurn runs one turn then self-sends
/// CheckContinue, which inspects the session leaf to decide ContinueTurn
/// (leaf is toolResult/user) or FinishRun (leaf is terminal assistant).
/// SteerCmd/SwitchLlmCmd are ordinary commands processed between turns
/// by FIFO — no side-channel queue. See docs/superpowers/plans/2026-07-14-agent-mailbox-driven-loop.md.
/// </para>
/// </summary>
public sealed class Agent : EventSourcedActor, ICancelable
{
    private readonly List<Llm> _llms = [];
    private readonly List<Tool> _tools = [];
    private readonly List<Guid> _childIds = [];
    private string? _turnId;
    private CancellationTokenSource? _turnCts;
    private int _runIterations;
    private const int MaxRunIterations = 100;

    internal string? TurnId => _turnId;

    // ── Turn-aware output ──

    private void WriteTurnOutput(
        string type,
        string? data = null,
        string? toolCallId = null,
        string? toolName = null
    ) => WriteOutput(type, data, toolCallId, toolName, _turnId);

    public Llm CurrentLlm { get; private set; } = null!;
    public IReadOnlyList<Llm> LlmsSnapshot => _llms;
    public IReadOnlyList<Tool> ToolsSnapshot => _tools;
    public AgentStatus Status { get; private set; }
    public Guid? ParentId { get; private set; }
    public IReadOnlyList<Guid> ChildIds => _childIds;
    public List<string>? Packages { get; private set; }
    public string? FailureReason { get; private set; }
    public Guid? SessionId { get; private set; }

    internal ILlmClient? LlmClient { get; init; }
    internal IToolRunner? ToolRunner { get; init; }
    public List<SkillInfo>? Skills { get; set; }
    public Session? Session { get; set; }

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
                        c.SessionId,
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
                if (ReferenceEquals(match, CurrentLlm))
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
            {
                if (Status != AgentStatus.Running)
                    throw new DomainInvariantException($"Cannot run turn when status is {Status}");
                if (_turnCts is not null)
                    throw new InvalidOperationException("A turn is already running");
                return RunTurnHandlerAsync(r);
            }

            case ContinueTurn c:
                return ContinueTurnHandlerAsync(c);

            case CheckContinue c:
                return CheckContinueHandlerAsync(c);

            case SteerCmd s:
                return SteerHandlerAsync(s);

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
        // Fail loudly instead of silently dropping the request. Previously a null
        // System made the whole method a no-op: no child, no event, no exception —
        // the caller believed the spawn succeeded.
        if (this.System is null)
            throw new InvalidOperationException(
                "SpawnChild requires an ActorSystem; this actor has none."
            );

        var child = await this.System.CreateAsync<Agent>(
            new CreateAgent(s.Llms, s.CurrentProvider, s.CurrentModel, Guid.CreateVersion7(), Id)
        );
        // Propagate the requested tools to the child. Previously the Tools field
        // was carried on the command but never applied, so children spawned empty.
        foreach (var tool in s.Tools)
            this.System.Send(child.Id, new AddToolCmd(tool));
        RaiseEvent(new ChildSpawned(child.Id));
        return default;
    }

    // ── Single turn streaming (innermost layer) ──

    private async Task<Message> StreamSingleTurnAsync(string turnLabel, CancellationToken ct)
    {
        var session = Session!;
        var ctx = await session.BuildContext();
        var prompt = SystemPromptBuilder.Build(ToolsSnapshot, Skills);
        ctx.Insert(0, new Message { Role = "system", Content = prompt });

        var assembler = new TurnResponseAssembler(turnLabel);

        await foreach (var evt in LlmClient!.StreamAsync(CurrentLlm, ctx, ToolsSnapshot, ct))
        {
            WriteTurnOutput(evt.Type, evt.Content, evt.ToolCallId, evt.ToolName);
            if (evt.Type == "done")
                break;
            if (evt.Type == "error")
                return ErrorAssistantMessage(evt.Content ?? "LLM error");
            assembler.Handle(evt);
        }

        return assembler.Build();
    }

    private static Message ErrorAssistantMessage(string errorMessage) =>
        new()
        {
            Role = "assistant",
            StopReason = "error",
            ErrorMessage = errorMessage,
            ContentBlocks = [],
        };

    // ── Tool execution (one turn's batch) ──

    private async Task ExecuteToolsAsync(ContentBlock[] toolCalls, CancellationToken ct)
    {
        var session = Session!;
        var tasks = toolCalls.Select(tc => ExecuteOneToolAsync(tc, ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var msg in results)
            await session.Append(new MessageEntry { Message = msg });
    }

    private async Task<Message> ExecuteOneToolAsync(ContentBlock tc, CancellationToken ct)
    {
        string toolResult;
        try
        {
            toolResult = await ToolRunner!.ExecuteAsync(tc.Name ?? "", tc.Arguments, ct);
        }
        catch (Exception ex)
        {
            toolResult = $"[ToolError: {ex.GetType().Name}] {ex.Message}";
        }
        WriteTurnOutput("tool_result", toolResult, tc.Id, tc.Name);
        return new Message
        {
            Role = "toolResult",
            ToolCallId = tc.Id,
            ToolName = tc.Name,
            ContentBlocks = [new ContentBlock { Type = "text", Text = toolResult }],
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    // ── RunTurn / ContinueTurn / CheckContinue handlers (async, return ValueTask) ──

    private async ValueTask<object?> RunTurnHandlerAsync(RunTurn r)
    {
        _turnId = r.TurnId;
        _turnCts = new CancellationTokenSource();
        _runIterations = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            StopToken,
            _turnCts.Token
        );

        // Append the user message, run the first turn, then hand off to CheckContinue
        var session = Session!;
        var userMsg = new Message
        {
            Role = "user",
            Content = r.Message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await session.Append(new MessageEntry { Message = userMsg });

        await SingleTurnAsync("0", linked.Token);
        // SingleTurnAsync calls FinishRun on error/cancel (clears _turnId); on success send CheckContinue
        if (_turnId is not null)
            this.System?.Send(Id, new CheckContinue(r.TurnId));
        return default;
    }

    private async ValueTask<object?> ContinueTurnHandlerAsync(ContinueTurn c)
    {
        if (_turnCts is null)
            return default; // run already ended or cancelled, ignore stale message
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            StopToken,
            _turnCts.Token
        );
        await SingleTurnAsync(_runIterations.ToString(), linked.Token);
        if (_turnId is not null)
            this.System?.Send(Id, new CheckContinue(c.TurnId));
        return default;
    }

    private async ValueTask<object?> CheckContinueHandlerAsync(CheckContinue c)
    {
        if (_turnCts is null)
            return default; // run already ended, ignore
        await CheckContinueAsync(c.TurnId);
        return default;
    }

    private async ValueTask<object?> SteerHandlerAsync(SteerCmd s)
    {
        var session = Session!;
        var userMsg = new Message
        {
            Role = "user",
            Content = s.Message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await session.Append(new MessageEntry { Message = userMsg });
        return default;
    }

    // ── Single turn (mid layer): stream + execute tools ──

    private async ValueTask SingleTurnAsync(string turnLabel, CancellationToken ct)
    {
        try
        {
            var session = Session!;
            var msg = await StreamSingleTurnAsync(turnLabel, ct);
            await session.Append(new MessageEntry { Message = msg });

            if (msg.StopReason == "error")
            {
                WriteTurnOutput("error", msg.ErrorMessage);
                // Error turn terminates the run, do not continue
                FinishRun();
                return;
            }

            var toolCalls =
                msg.ContentBlocks?.Where(cb => cb.Type == "tool_call").ToArray() ?? [];
            if (toolCalls.Length > 0)
                await ExecuteToolsAsync(toolCalls, ct);
        }
        catch (OperationCanceledException) when (!StopToken.IsCancellationRequested)
        {
            // Turn cancelled (not actor stop) — emit cancelled, clean up run state, do not send CheckContinue.
            // Mirrors the old RunTurnAsync catch(OCE) when(!StopToken) semantics.
            // Must call FinishRun to clear _turnCts, otherwise the next RunTurn throws "A turn is already running".
            WriteTurnOutput("cancelled");
            FinishRun();
        }
        // actor-stop OCE (StopToken cancelled) is NOT caught — propagates out, RunAsync swallows it silently, no error emitted.
        // Mirrors the old RunTurnAsync catch(OCE) { throw; } + Stop_DuringRunTurn_NoErrorOutput test.
    }

    // ── Continue decision (session leaf is the single source of truth) ──

    private async ValueTask CheckContinueAsync(string turnId)
    {
        var session = Session!;
        var leaf = await session.GetLeafEntryAsync();

        // leaf is toolResult or user (steer injected) → still has pending input to respond to, continue
        if (leaf is MessageEntry me && me.Message.Role is "toolResult" or "user")
        {
            if (++_runIterations >= MaxRunIterations)
            {
                WriteTurnOutput(
                    "error",
                    "Run stopped: iteration limit reached (100 tool rounds)."
                );
                FinishRun();
                return;
            }
            this.System?.Send(Id, new ContinueTurn(turnId));
            return;
        }

        // leaf is assistant (no-tool terminal state) → run finished
        FinishRun();
    }

    private void FinishRun()
    {
        WriteTurnOutput("done");
        OutputWriter?.Complete();
        _turnId = null;
        _turnCts?.Dispose();
        _turnCts = null;
        _runIterations = 0;
    }

    // ── Turn cancellation ──

    /// <inheritdoc cref="ICancelable.CancelCurrentTurn"/>
    public void CancelCurrentTurn()
    {
        _turnCts?.Cancel();
    }

    // ── State recovery ──

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
                SessionId = c.SessionId;
                ParentId = c.ParentId;
                Packages = c.Packages;
                Status = AgentStatus.Pending;
                Session = new Session(c.SessionId, storage: new InMemorySessionStorage());
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
