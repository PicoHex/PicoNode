namespace PicoNode.Agent.Domain;

/// <summary>
/// Agent aggregate root. Inherits EventSourcedActor — state is managed via
/// domain events (RaiseEvent), persisted by the Actor framework, and
/// restored via ReplayEvents. Session is data (not state), managed separately.
/// </summary>
public sealed class Agent : EventSourcedActor, ICancelable
{
    private readonly List<Llm> _llms = [];
    private readonly List<Tool> _tools = [];
    private readonly List<Guid> _childIds = [];
    private string? _turnId;
    private CancellationTokenSource? _turnCts;

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
                _turnId = r.TurnId;
                _turnCts = new CancellationTokenSource();
                return RunTurnAsync(r.Message, _turnCts.Token);
            }

            case ContinueTurn c:
                // 实际续跑逻辑在 Task 5 接入; 此处占位以保证命令可派发
                return default;

            case CheckContinue c:
                // 实际决策逻辑在 Task 5 接入; 此处占位
                return default;

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
        foreach (var tc in toolCalls)
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

            var toolMsg = new Message
            {
                Role = "toolResult",
                ToolCallId = tc.Id,
                ToolName = tc.Name,
                ContentBlocks = [new ContentBlock { Type = "text", Text = toolResult }],
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await session.Append(new MessageEntry { Message = toolMsg });
            WriteTurnOutput("tool_result", toolResult, tc.Id, tc.Name);
        }
    }

    // ── RunTurn ──

    private async ValueTask<object?> RunTurnAsync(string message, CancellationToken turnCt)
    {
        if (LlmClient is null || ToolRunner is null)
            throw new InvalidOperationException(
                "RunTurn requires LlmClient and ToolRunner — register them via the Agent constructor."
            );

        // Link turn cancellation with actor stop cancellation
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(StopToken, turnCt);
        var ct = linked.Token;

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
                {
                    // Surface the guard as an error instead of silently breaking and
                    // emitting "done" — otherwise a truncated turn looks like success.
                    WriteTurnOutput(
                        "error",
                        "Turn stopped: iteration limit reached (100 tool rounds)."
                    );
                    return default;
                }

                var finalMessage = await StreamSingleTurnAsync($"{iterations}", ct);
                await session.Append(new MessageEntry { Message = finalMessage });

                if (finalMessage.StopReason == "error")
                {
                    WriteTurnOutput("error", finalMessage.ErrorMessage);
                    // turn 以错误结束,不再继续工具循环
                    return default;
                }

                // Check for tool calls
                var toolCalls =
                    finalMessage.ContentBlocks?.Where(cb => cb.Type == "tool_call").ToArray() ?? [];
                hasTools = toolCalls.Length > 0;

                if (hasTools)
                    await ExecuteToolsAsync(toolCalls, ct);
            } while (hasTools && !ct.IsCancellationRequested);

            WriteTurnOutput("done");
            return default;
        }
        catch (OperationCanceledException) when (!StopToken.IsCancellationRequested)
        {
            // Turn was cancelled (not the whole actor)
            WriteTurnOutput("cancelled");
            return default;
        }
        catch (OperationCanceledException)
        {
            // Actor is stopping — propagate silently, not an error
            throw;
        }
        catch (Exception ex)
        {
            WriteTurnOutput("error", ex.Message);
            throw;
        }
        finally
        {
            OutputWriter?.Complete();
            _turnId = null;
            _turnCts?.Dispose();
            _turnCts = null;
        }
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
