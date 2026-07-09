namespace PicoNode.Actor.Abs;

/// <summary>
/// Base class for Event-Sourced actors.
/// Inherits Actor; adds RaiseEvent, event management, and version tracking.
///
/// RaiseEvent records the event AND immediately mutates state — the actor is the
/// single authority over its own version and in-memory state. Persistence is
/// a separate concern handled by the runtime layer after the fact.
/// </summary>
public abstract class EventSourcedActor : Actor, IEventSourcedActor
{
    private readonly List<IDomainEvent> _events = new();

    /// <inheritdoc/>
    public ulong Version { get; protected set; }

    /// <summary>Creation path. Chains to Actor(ICommand).</summary>
    protected EventSourcedActor(ICommand creationCommand)
        : base(creationCommand) { }

    /// <summary>Rebuild path. Chains to Actor().</summary>
    protected EventSourcedActor() { }

    /// <summary>
    /// Record a domain event, mutate in-memory state, and increment Version.
    /// Called by subclasses within OnMessageAsync.
    /// The actor manages its own version — each RaiseEvent is one version increment.
    /// </summary>
    protected void RaiseEvent<T>(T @event)
        where T : notnull, IDomainEvent
    {
        _events.Add(@event);
        Mutate(@event);
        Version++;
    }

    /// <summary>
    /// Mutate in-memory state from a domain event.
    /// Called by RaiseEvent (normal flow) and ReplayEvents (recovery flow).
    /// </summary>
    protected abstract void Mutate(IDomainEvent @event);

    IReadOnlyList<IDomainEvent> IEventSourcedActor.GetUncommittedEvents() => _events.AsReadOnly();

    void IEventSourcedActor.CommitEvents() => _events.Clear();

    void IEventSourcedActor.ReplayEvents(IReadOnlyList<IDomainEvent> events)
    {
        foreach (var e in events)
            Mutate(e);
        Version = (ulong)events.Count;
    }
}
