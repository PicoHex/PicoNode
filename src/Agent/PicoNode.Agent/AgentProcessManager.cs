namespace PicoNode.Agent.Domain;

// ── AgentProcessManager events ──
public sealed record PmAgentCreationValidated(string Name, Guid LlmId) : DomainEvent;
public sealed record PmAgentCreationCompleted(Guid AgentId) : DomainEvent;
public sealed record PmAgentDeletionStarted(Guid AgentId) : DomainEvent;
public sealed record PmAgentRuntimesStopped(int Count) : DomainEvent;
public sealed record PmAgentDeletionCompleted(Guid AgentId) : DomainEvent;

public sealed class AgentProcessManager : EventSourcedActor
{
    private int _step;
    private bool _completed;
    private Guid _targetId;
    private string _pendingName = "";

    public AgentProcessManager() { }

    protected override async ValueTask OnReadyAsync()
    {
        await base.OnReadyAsync();

        if (!_completed && _step > 0 && _targetId != Guid.Empty)
        {
            if (_step <= 2)
                await OnMessageAsync(new CreateAgentCmd(_pendingName, _targetId));
            else
                await OnMessageAsync(new DeleteAgentCmd(_targetId));
        }
    }

    protected override async ValueTask<object?> OnMessageAsync(ICommand command)
    {
        if (_completed)
            throw new InvalidOperationException("PM already completed");

        switch (command)
        {
            case CreateAgentCmd c:
                await HandleCreateAgent(c.Name, c.LlmId);
                return default;
            case DeleteAgentCmd c:
                await HandleDeleteAgent(c.AgentId);
                return default;
            default:
                throw new DomainInvariantException($"Unknown: {command.GetType().Name}");
        }
    }

    private async ValueTask HandleCreateAgent(string name, Guid llmId)
    {
        await System!.AskAsync<LlmData>(llmId, new GetLlmDataQuery());
        RaiseEvent(new PmAgentCreationValidated(name, llmId));
        var agent = await System.CreateAsync<Agent>(new CreateAgent(name, llmId));
        RaiseEvent(new PmAgentCreationCompleted(agent.Id));
        _completed = true;
    }

    private async ValueTask HandleDeleteAgent(Guid agentId)
    {
        RaiseEvent(new PmAgentDeletionStarted(agentId));
        // TODO: FindSessionsByAgent — ActorSystem query support (Task 9)
        System!.Send(agentId, new DeleteAgent());
        RaiseEvent(new PmAgentDeletionCompleted(agentId));
        _completed = true;
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case PmAgentCreationValidated e: _step = 1; _targetId = e.LlmId; _pendingName = e.Name; break;
            case PmAgentCreationCompleted: _step = 2; _completed = true; break;
            case PmAgentDeletionStarted e: _step = 3; _targetId = e.AgentId; break;
            case PmAgentDeletionCompleted: _step = 5; _completed = true; break;
        }
    }
}
