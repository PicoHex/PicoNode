using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

// ═══════════════════════════════════════════════════════════
// Domain Events
// ═══════════════════════════════════════════════════════════

public sealed record AgentCreated(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    string HomeDir,
    Guid? ParentId,
    List<string>? Packages
) : IDomainEvent;

public sealed record AgentStarted : IDomainEvent;
public sealed record AgentCompleted : IDomainEvent;
public sealed record AgentFailed(string Reason) : IDomainEvent;
public sealed record LlmSwitched(string ProviderName, string ModelId) : IDomainEvent;
public sealed record LlmAdded(Llm Llm) : IDomainEvent;
public sealed record LlmRemoved(string ProviderName, string ModelId) : IDomainEvent;
public sealed record ToolAdded(Tool Tool) : IDomainEvent;
public sealed record ToolRemoved(string Name) : IDomainEvent;
public sealed record ChildSpawned(Guid ChildId) : IDomainEvent;

// ═══════════════════════════════════════════════════════════
// Commands
// ═══════════════════════════════════════════════════════════

public sealed record CreateAgent(
    List<Llm> Llms,
    string CurrentProvider,
    string CurrentModel,
    string HomeDir,
    Guid? ParentId = null,
    List<string>? Packages = null
) : ICommand;

public sealed record RunTurn(string Message) : ICommand;

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

// ═══════════════════════════════════════════════════════════
// Agent Actor
// ═══════════════════════════════════════════════════════════

public sealed class NewAgent : EventSourcedActor
{
    private readonly List<Llm> _llms = [];
    private readonly List<Tool> _tools = [];
    private readonly List<Guid> _childIds = [];

    // State
    public Llm CurrentLlm { get; private set; } = null!;
    public IReadOnlyList<Llm> LlmsSnapshot => _llms;
    public IReadOnlyList<Tool> ToolsSnapshot => _tools;
    public AgentStatus Status { get; private set; }
    public Guid? ParentId { get; private set; }
    public IReadOnlyList<Guid> ChildIds => _childIds;
    public List<string>? Packages { get; private set; }
    public string HomeDir { get; private set; } = null!;
    public string? FailureReason { get; private set; }

    // Infrastructure (set by ActorSystem)
    internal ILlmClient? LlmClient { get; set; }
    internal IToolRunner? ToolRunner { get; set; }

    // Session (data, not ES)
    public Session? Session { get; private set; }

    // ── Constructors ──

    public NewAgent(CreateAgent cmd)
        : base(cmd) { }

    public NewAgent(
        CreateAgent cmd,
        ILlmClient llmClient,
        IToolRunner toolRunner
    )
        : base(cmd)
    {
        LlmClient = llmClient;
        ToolRunner = toolRunner;
    }

    /// <summary>Rebuild constructor — infrastructure injected by factory.</summary>
    public NewAgent(ILlmClient llmClient, IToolRunner toolRunner)
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
                    throw new DomainInvariantException(
                        $"Cannot start from {Status}"
                    );
                RaiseEvent(new AgentStarted());
                return default;

            case CompleteAgent:
                if (Status != AgentStatus.Running)
                    throw new DomainInvariantException(
                        $"Cannot complete from {Status}"
                    );
                RaiseEvent(new AgentCompleted());
                return default;

            case FailAgent f:
                if (Status != AgentStatus.Running)
                    throw new DomainInvariantException(
                        $"Cannot fail from {Status}"
                    );
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
                    throw new DomainInvariantException(
                        "Cannot remove the only Llm"
                    );
                var match = _llms.FirstOrDefault(l =>
                    l.ProviderName == r.ProviderName && l.ModelId == r.ModelId
                );
                if (match is null)
                    throw new DomainInvariantException(
                        $"Llm not found: {r.ProviderName}/{r.ModelId}"
                    );
                if (match.Equals(CurrentLlm))
                    throw new DomainInvariantException(
                        "Cannot remove CurrentLlm; switch first"
                    );
                RaiseEvent(new LlmRemoved(r.ProviderName, r.ModelId));
                return default;

            case AddToolCmd a:
                if (_tools.Any(t => t.Name == a.Tool.Name))
                    throw new DomainInvariantException(
                        $"Tool already exists: {a.Tool.Name}"
                    );
                RaiseEvent(new ToolAdded(a.Tool));
                return default;

            case RemoveToolCmd r:
                RaiseEvent(new ToolRemoved(r.Name));
                return default;

            case SpawnChildCmd s:
            {
                return SpawnChildAsync(s);
            }

            case RunTurn r:
                return RunTurnAsync(r.Message, StopToken);
        }

        return default;
    }

    // ── RunTurn: nested do...while LLM + tool loop ──

    private async ValueTask<object?> SpawnChildAsync(SpawnChildCmd s)
    {
        if (this.System is not null)
        {
            var child = await this.System.CreateAsync<NewAgent>(
                new CreateAgent(s.Llms, s.CurrentProvider, s.CurrentModel, HomeDir, Id));
            RaiseEvent(new ChildSpawned(child.Id));
        }
        return default;
    }

    private async ValueTask<object?> RunTurnAsync(string message, CancellationToken ct)
    {
        var session = Session!;

        // Append user message
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
            var response = await LlmClient!.CompleteAsync(
                CurrentLlm, ctx, ToolsSnapshot, ct
            );

            // Handle empty/error response
            if (response.ContentBlocks is not { Length: > 0 })
            {
                response.ContentBlocks =
                [
                    new ContentBlock
                    {
                        Type = "text",
                        Text = "[No response from LLM]",
                    },
                ];
            }

            await session.Append(new MessageEntry { Message = response });

            var toolCalls = response
                .ContentBlocks?.Where(cb => cb.Type == "tool_call")
                .ToArray();
            hasTools = toolCalls is { Length: > 0 };

            if (hasTools)
            {
                foreach (var tc in toolCalls!)
                {
                    var toolResult = await ToolRunner!.ExecuteAsync(
                        tc.Name ?? "", tc.Arguments, ct
                    );
                    var toolMsg = new Message
                    {
                        Role = "toolResult",
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        ContentBlocks =
                        [
                            new ContentBlock
                            {
                                Type = "text",
                                Text = toolResult,
                            },
                        ],
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    await session.Append(new MessageEntry { Message = toolMsg });
                }
            }
        } while (hasTools && !ct.IsCancellationRequested);

        return default;
    }

    // ── State recovery ──

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case AgentCreated c:
                _llms.Clear();
                _llms.AddRange(c.Llms);
                CurrentLlm =
                    c.Llms.First(l =>
                        l.ProviderName == c.CurrentProvider
                        && l.ModelId == c.CurrentModel
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
                CurrentLlm =
                    _llms.First(l =>
                        l.ProviderName == s.ProviderName && l.ModelId == s.ModelId
                    );
                break;

            case LlmAdded a:
                _llms.Add(a.Llm);
                break;

            case LlmRemoved r:
                _llms.RemoveAll(l =>
                    l.ProviderName == r.ProviderName && l.ModelId == r.ModelId
                );
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
}

// ═══════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════

/// <summary>Fake ILlmClient for unit tests — never actually called.</summary>
internal sealed class FakeLlmClient : ILlmClient
{
    public Task<Message> CompleteAsync(
        Llm llm,
        List<Message> context,
        IReadOnlyList<Tool> tools,
        CancellationToken ct
    ) => Task.FromResult(new Message());
}

/// <summary>Fake IToolRunner for unit tests.</summary>
internal sealed class FakeToolRunner : IToolRunner
{
    public Task<string> ExecuteAsync(
        string toolName,
        Dictionary<string, object?> args,
        CancellationToken ct
    ) => Task.FromResult("ok");
}

public sealed class NewAgentTests
{
    [Test]
    public async Task Create_And_Start_TransitionsToRunning()
    {
        var store = new InMemoryEventStore();
        var system = new ActorSystem(store);

        var llms = new List<Llm>
        {
            new()
            {
                ProviderName = "deepseek",
                ModelId = "deepseek-chat",
                ApiKey = "sk-test",
            },
        };

        system.Register<NewAgent>(
            cmd =>
                cmd switch
                {
                    CreateAgent c => new NewAgent(c),
                    _ => throw new InvalidOperationException(),
                },
            () => new NewAgent(new FakeLlmClient(), new FakeToolRunner())
        );

        var agent = await system.CreateAsync<NewAgent>(
            new CreateAgent(llms, "deepseek", "deepseek-chat", "/tmp")
        );

        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Pending);

        // Send Start command through mailbox
        system.Send(agent.Id, new StartAgent());
        await Task.Delay(100);

        await Assert.That(agent.Status).IsEqualTo(AgentStatus.Running);
    }
}
