namespace PicoNode.Actor.Abs;

/// <summary>
/// Base class for Event-Sourced actors.
/// Inherits Actor; adds ApplyEvent, event management, and version tracking.
/// </summary>
public abstract class EventSourcedActor : Actor, IEventSourcedActor
{
    private readonly List<IDomainEvent> _events = new();

    /// <inheritdoc/>
    public ulong Version { get; protected set; }

    protected EventSourcedActor(Guid id) : base(id) { }

    /// <summary>
    /// Apply a domain event. Registers the event and immediately mutates state.
    /// Called by subclasses within OnMessageAsync.
    /// </summary>
    protected void ApplyEvent<T>(T @event) where T : notnull, IDomainEvent
    {
        _events.Add(@event);
        Mutate(@event);
        Version++;
    }

    /// <summary>Override to mutate state based on a domain event.</summary>
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
