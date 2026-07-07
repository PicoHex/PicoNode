namespace PicoNode.Agent.Domain;

public sealed class Agent
{
    private readonly List<Llm> _llms;
    private readonly List<Tool> _tools;
    private readonly List<Guid> _childIds;

    public Guid Id { get; }
    public IReadOnlyList<Llm> Llms => _llms;
    public Llm CurrentLlm { get; private set; }
    public IReadOnlyList<Tool> Tools => _tools;
    public AgentStatus Status { get; private set; }
    public Guid? ParentId { get; }
    public IReadOnlyList<Guid> ChildIds => _childIds;
    public List<string>? Packages { get; }
    public string HomeDir { get; }
    public Session Session { get; }
    public string? FailureReason { get; private set; }

    public Agent(
        Guid id,
        List<Llm> llms,
        string currentProvider,
        string currentModel,
        string homeDir,
        Guid? parentId = null,
        List<string>? packages = null,
        ISessionStorage? sessionStorage = null
    )
    {
        if (llms is null || llms.Count == 0)
            throw new DomainInvariantException("Invariant 1: at least one Llm required");
        if (llms.Any(l => string.IsNullOrEmpty(l.ApiKey)))
            throw new DomainInvariantException("Invariant 1: all Llms must have ApiKey");

        Id = id;
        _llms = new List<Llm>(llms);
        _tools = [];
        _childIds = [];
        Status = AgentStatus.Pending;
        ParentId = parentId;
        HomeDir = homeDir;
        Packages = packages;
        Session = new Session(Guid.CreateVersion7(), sessionStorage);

        var match = _llms.FirstOrDefault(l =>
            l.ProviderName == currentProvider && l.ModelId == currentModel
        );
        if (match is null)
            throw new DomainInvariantException(
                $"Invariant 2: CurrentLlm ({currentProvider}/{currentModel}) not in Llms"
            );
        CurrentLlm = match;
    }

    // ── Lifecycle ──

    public void Start()
    {
        if (Status != AgentStatus.Pending)
            throw new DomainInvariantException($"Invariant 6: cannot start from {Status}");
        Status = AgentStatus.Running;
    }

    public void Complete()
    {
        if (Status != AgentStatus.Running)
            throw new DomainInvariantException($"Invariant 6: cannot complete from {Status}");
        Status = AgentStatus.Completed;
    }

    public void Fail(string reason)
    {
        if (Status != AgentStatus.Running)
            throw new DomainInvariantException($"Invariant 6: cannot fail from {Status}");
        Status = AgentStatus.Failed;
        FailureReason = reason;
    }

    public Agent SpawnChild(
        List<Llm> llms,
        string currentProvider,
        string currentModel,
        List<Tool> tools
    )
    {
        var child = new Agent(
            Guid.CreateVersion7(),
            llms,
            currentProvider,
            currentModel,
            HomeDir,
            Id
        );
        foreach (var t in tools)
            child.AddTool(t);
        _childIds.Add(child.Id);
        return child;
    }

    // ── Llm management ──

    public void SwitchLlm(string providerName, string modelId)
    {
        var match = _llms.FirstOrDefault(l =>
            l.ProviderName == providerName && l.ModelId == modelId
        );
        if (match is null)
            throw new DomainInvariantException(
                $"Invariant 2: Llm not found: {providerName}/{modelId}"
            );
        CurrentLlm = match;
    }

    public void AddLlm(Llm llm)
    {
        if (string.IsNullOrEmpty(llm.ApiKey))
            throw new DomainInvariantException("Invariant 1: ApiKey required");
        _llms.Add(llm);
    }

    public void RemoveLlm(string providerName, string modelId)
    {
        if (_llms.Count <= 1)
            throw new DomainInvariantException("Invariant 7: cannot remove the only Llm");
        var match = _llms.FirstOrDefault(l =>
            l.ProviderName == providerName && l.ModelId == modelId
        );
        if (match is null)
            throw new DomainInvariantException($"Llm not found: {providerName}/{modelId}");
        if (match.Equals(CurrentLlm))
            throw new DomainInvariantException(
                "Invariant 7: cannot remove CurrentLlm; switch first"
            );
        _llms.Remove(match);
    }

    // ── Tool management ──

    public void AddTool(Tool tool)
    {
        if (_tools.Any(t => t.Name == tool.Name))
            throw new DomainInvariantException($"Invariant 3: Tool already exists: {tool.Name}");
        _tools.Add(tool);
    }

    public void RemoveTool(string name) => _tools.RemoveAll(t => t.Name == name);

    // ── Turn execution ──

    public Task<List<Message>> RunTurn(
        string message,
        ILlmClient llmClient,
        IToolRunner toolRunner,
        CancellationToken ct
    ) => RunTurn(message, llmClient, toolRunner, ct, null);

    public async Task<List<Message>> RunTurn(
        string message,
        ILlmClient llmClient,
        IToolRunner toolRunner,
        CancellationToken ct,
        Func<string, string?, Task>? onEvent
    )
    {
        var result = new List<Message>();

        var userMsg = new Message
        {
            Role = "user",
            Content = message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await Session.Append(new MessageEntry { Message = userMsg });
        result.Add(userMsg);

        if (onEvent is not null)
            await onEvent("text", message);

        bool hasTools;
        var iterations = 0;
        do
        {
            if (++iterations > 100)
                break;
            var ctx = await Session.BuildContext();
            var response = await llmClient.CompleteAsync(CurrentLlm, ctx, Tools, ct);

            await Session.Append(new MessageEntry { Message = response });
            result.Add(response);

            var respText = response.ContentBlocks?.FirstOrDefault(cb => cb.Type == "text")?.Text;
            if (respText is not null && onEvent is not null)
                await onEvent("text", respText);

            var toolCalls = response.ContentBlocks?.Where(cb => cb.Type == "tool_call").ToArray();
            hasTools = toolCalls is { Length: > 0 };

            if (hasTools)
            {
                foreach (var tc in toolCalls!)
                {
                    var toolResult = await toolRunner.ExecuteAsync(tc.Name ?? "", tc.Arguments, ct);
                    var toolMsg = new Message
                    {
                        Role = "toolResult",
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        ContentBlocks = [new ContentBlock { Type = "text", Text = toolResult }],
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    await Session.Append(new MessageEntry { Message = toolMsg });
                    result.Add(toolMsg);
                }
            }
        } while (hasTools);

        if (onEvent is not null)
            await onEvent("done", null);

        return result;
    }
}
