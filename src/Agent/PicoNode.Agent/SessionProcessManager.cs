namespace PicoNode.Agent.Domain;

// ── SessionProcessManager events ──
// [PicoDerivedType] registrations in DomainEvent.cs
public sealed record PmSessionCreationStarted(string Name, Guid AgentId) : DomainEvent;
public sealed record PmSessionActorCreated(Guid SessionId) : DomainEvent;
public sealed record PmRuntimeActorCreated(Guid SessionId) : DomainEvent;
public sealed record PmSessionDeletionStarted(Guid SessionId) : DomainEvent;
public sealed record PmSessionDeletionCompleted(Guid SessionId) : DomainEvent;

public sealed class SessionProcessManager : EventSourcedActor
{
    private int _step;
    private bool _completed;
    private Guid _targetId;
    private string _pendingName = "";

    public SessionProcessManager() { }

    protected override async ValueTask OnReadyAsync()
    {
        await base.OnReadyAsync();

        if (!_completed && _step > 0 && _targetId != Guid.Empty)
        {
            if (_step <= 3)
                await OnMessageAsync(new CreateSessionCmd(_pendingName, _targetId));
            else
                await OnMessageAsync(new DeleteSessionCmd(_targetId));
        }
    }

    protected override async ValueTask<object?> OnMessageAsync(ICommand command)
    {
        if (_completed)
            throw new InvalidOperationException("PM already completed");

        switch (command)
        {
            case CreateSessionCmd c:
                await HandleCreateSession(c.Name, c.AgentId);
                return default;
            case DeleteSessionCmd c:
                await HandleDeleteSession(c.SessionId);
                return default;
            default:
                throw new DomainInvariantException($"Unknown: {command.GetType().Name}");
        }
    }

    private async ValueTask HandleCreateSession(string name, Guid agentId)
    {
        RaiseEvent(new PmSessionCreationStarted(name, agentId));
        var session = await System!.CreateAsync<SessionActor>(new StartSession(name));
        RaiseEvent(new PmSessionActorCreated(session.Id));
        var runtime = await System.CreateAsync<RuntimeActor>(new InitRuntimeCmd(agentId, session.Id));
        System.Send(runtime.Id, new InitRuntimeCmd(agentId, session.Id));
        RaiseEvent(new PmRuntimeActorCreated(runtime.Id));
        _completed = true;
    }

    private async ValueTask HandleDeleteSession(Guid sessionId)
    {
        RaiseEvent(new PmSessionDeletionStarted(sessionId));
        System!.Send(sessionId, new DeleteSession());
        RaiseEvent(new PmSessionDeletionCompleted(sessionId));
        _completed = true;
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case PmSessionCreationStarted e: _step = 1; _targetId = e.AgentId; _pendingName = e.Name; break;
            case PmSessionActorCreated: _step = 2; break;
            case PmRuntimeActorCreated: _step = 3; _completed = true; break;
            case PmSessionDeletionStarted e: _step = 4; _targetId = e.SessionId; break;
            case PmSessionDeletionCompleted: _step = 5; _completed = true; break;
        }
    }
}
