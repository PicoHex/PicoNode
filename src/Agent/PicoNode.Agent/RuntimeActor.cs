namespace PicoNode.Agent.Domain;

/// <summary>
/// Per-session execution loop Process Manager.
/// Non-ES Actor (no persistence) — runtime state is rebuilt per session.
/// </summary>
public sealed class RuntimeActor : PicoNode.Actor.Abs.Actor
{
    private Guid _agentId;
    private Guid _sessionId;

    /// <summary>External dependencies injected after construction.</summary>
    public ILlmClient? LlmClient { get; set; }
    public IToolRunner? ToolRunner { get; set; }

    public Guid AgentId => _agentId;
    public Guid SessionId => _sessionId;

    public RuntimeActor() { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
    {
        return command switch
        {
            InitRuntimeCmd c => HandleInit(c),
            RunTurnCmd c => HandleRunTurn(c),
            GetLoadedConfigQuery => new ValueTask<object?>(new { _agentId, _sessionId }),
            _ => throw new DomainInvariantException($"Unknown: {command.GetType().Name}")
        };
    }

    private ValueTask<object?> HandleInit(InitRuntimeCmd c)
    {
        _agentId = c.AgentId;
        _sessionId = c.SessionId;
        return default;
    }

    private async ValueTask<object?> HandleRunTurn(RunTurnCmd c)
    {
        if (System is null)
            throw new InvalidOperationException("Runtime not initialized: System is null");

        try
        {
            // Fetch agent config
            var config = await System.AskAsync<AgentConfigSnapshot>(_agentId, new GetConfigQuery());

            // Fetch session context
            var sessionCtx = await System.AskAsync<SessionContext>(_sessionId, new GetContextQuery());

            // Append user message to session
            var userMsg = new Message
            {
                Role = "user",
                Content = c.Message,
                ContentBlocks = [new ContentBlock { Type = "text", Text = c.Message }],
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            System.Send(_sessionId, new AppendMessage(userMsg));

            // Simple single-turn response
            var assistantMsg = new Message
            {
                Role = "assistant",
                Content = $"Echo: {c.Message}",
                ContentBlocks = [new ContentBlock { Type = "text", Text = $"Echo: {c.Message}" }],
                Sender = new Sender(_agentId, config.Name),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                StopReason = "stop"
            };
            System.Send(_sessionId, new AppendMessage(assistantMsg));

            WriteOutput("done", turnId: Guid.CreateVersion7().ToString("N")[..8]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteOutput("error", $"Turn failed: {ex.Message}");
        }

        return default;
    }
}
