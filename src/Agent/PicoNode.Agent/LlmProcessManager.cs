namespace PicoNode.Agent.Domain;

// ── LlmProcessManager events ──
public sealed record PmLlmDeletionStarted(Guid LlmId) : DomainEvent;
public sealed record PmLlmDeletionCompleted : DomainEvent;
public sealed record PmSystemLlmChangeStarted(Guid LlmId) : DomainEvent;
public sealed record PmOldSystemLlmDemoted(Guid? LlmId) : DomainEvent;
public sealed record PmSystemLlmPromoted(Guid LlmId) : DomainEvent;

public sealed class LlmProcessManager : EventSourcedActor
{
    private int _step;
    private bool _completed;
    private Guid _targetId;

    public LlmProcessManager() { }

    protected override async ValueTask OnReadyAsync()
    {
        await base.OnReadyAsync();

        if (!_completed && _step > 0 && _targetId != Guid.Empty)
        {
            if (_step <= 3)
                await OnMessageAsync(new DeleteLlmCmd(_targetId));
            else if (_step <= 6)
                await OnMessageAsync(new SetSystemLlmCmd(_targetId));
        }
    }

    protected override async ValueTask<object?> OnMessageAsync(ICommand command)
    {
        if (_completed)
            throw new InvalidOperationException("PM already completed");

        switch (command)
        {
            case DeleteLlmCmd c:
                await HandleDeleteLlm(c.LlmId);
                return default;
            case SetSystemLlmCmd c:
                await HandleSetSystemLlm(c.LlmId);
                return default;
            default:
                throw new DomainInvariantException($"Unknown: {command.GetType().Name}");
        }
    }

    private async ValueTask HandleDeleteLlm(Guid llmId)
    {
        RaiseEvent(new PmLlmDeletionStarted(llmId));
        var data = await System!.AskAsync<LlmData>(llmId, new GetLlmDataQuery());
        if (data.IsSystem)
            throw new DomainInvariantException("Cannot delete system LLM");

        // TODO: FindAgentsByLlm — ActorSystem query support (Task 9)
        RaiseEvent(new PmLlmDeletionCompleted());
        System.Send(llmId, new DeleteLlm());
        _completed = true;
    }

    private async ValueTask HandleSetSystemLlm(Guid llmId)
    {
        RaiseEvent(new PmSystemLlmChangeStarted(llmId));
        // TODO: FindSystemLlm — ActorSystem query support (Task 9)
        RaiseEvent(new PmSystemLlmPromoted(llmId));
        System!.Send(llmId, new PromoteSystemLlmCmd());
        _completed = true;
    }

    protected override void Mutate(IDomainEvent @event)
    {
        switch (@event)
        {
            case PmLlmDeletionStarted e: _step = 1; _targetId = e.LlmId; break;
            case PmLlmDeletionCompleted: _step = 3; _completed = true; break;
            case PmSystemLlmChangeStarted e: _step = 4; _targetId = e.LlmId; break;
            case PmSystemLlmPromoted: _step = 6; _completed = true; break;
        }
    }
}
