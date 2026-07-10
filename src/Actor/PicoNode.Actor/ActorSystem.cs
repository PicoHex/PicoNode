using PicoLog.Abs;
using PicoNode.Actor.Abs;
using ActorBase = PicoNode.Actor.Abs.Actor;

namespace PicoNode.Actor;

/// <summary>
/// Default IActorSystem implementation. Manages actor lifecycle:
/// creation, retrieval, message routing, and stopping.
/// Registered as a PicoDI Singleton.
/// </summary>
public sealed class ActorSystem : IActorSystem
{
    private readonly ConcurrentDictionary<Guid, ActorBase> _registry = new();
    private readonly ConcurrentDictionary<Type, Func<ICommand, object>> _factories = new();
    private readonly IEventStore _eventStore;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates an ActorSystem with the given event store and optional logger.
    /// Pass <see cref="InMemoryEventStore"/> for pure in-memory mode.
    /// </summary>
    public ActorSystem(IEventStore eventStore, ILogger? logger = null)
    {
        _eventStore = eventStore;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════
    // Registration
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Register<T>(Func<ICommand, T> factory)
        where T : IActor
    {
        _factories[typeof(T)] = cmd => factory(cmd)!;
    }

    // ═══════════════════════════════════════════════════════════
    // Creation
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<T> CreateAsync<T>(ICommand command)
        where T : IActor
    {
        if (!_factories.TryGetValue(typeof(T), out var factory))
            throw new InvalidOperationException(
                $"No factory registered for {typeof(T).Name}. Call Register<T> first."
            );

        // 1. Call factory with creation command → constructor processes atomically
        var actor = (ActorBase)factory(command);

        // 2. Assign framework-generated UUID v7
        var id = Guid.CreateVersion7();
        actor.Id = id;

        // 3. Wire event store if this is an ES actor
        if (actor is EventSourcedActor es)
            es.EventStore = _eventStore;

        // 4. Register in the system
        _registry[id] = actor;

        // 5. Release the consumption loop gate
        actor.SignalReady();

        _logger?.Info($"Actor {typeof(T).Name} created: {id}");

        return new ValueTask<T>((T)(IActor)actor);
    }

    // ═══════════════════════════════════════════════════════════
    // Retrieval
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<T?> GetAsync<T>(Guid id)
        where T : IActor, new()
    {
        // In-memory hit — returns immediately
        if (_registry.TryGetValue(id, out var existing))
            return (T)(IActor)existing;

        // Non-ES actors are ephemeral — cannot be rebuilt, return null
        if (!typeof(IEventSourcedActor).IsAssignableFrom(typeof(T)))
            return default;

        // Load events from store
        var events = await _eventStore.LoadAsync(id).ConfigureAwait(false);
        if (events.Count == 0)
            return default;

        // Rebuild: parameterless constructor (AOT-safe new() constraint)
        var actor = (ActorBase)(IActor)new T();
        actor.Id = id;

        var es = (IEventSourcedActor)actor;
        ((EventSourcedActor)actor).EventStore = _eventStore;
        es.ReplayEvents(events);

        // TryAdd: if another thread already rebuilt this actor, discard ours
        if (!_registry.TryAdd(id, actor))
        {
            _logger?.Warning(
                $"Actor {typeof(T).Name} {id} already rebuilt by another thread, discarding duplicate"
            );
            await actor.StopAsync().ConfigureAwait(false);
            return (T)(IActor)_registry[id];
        }

        actor.SignalReady();

        _logger?.Info($"Actor {typeof(T).Name} rebuilt from events: {id} (v{es.Version})");

        return (T)(IActor)actor;
    }

    // ═══════════════════════════════════════════════════════════
    // Messaging
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Send(Guid id, ICommand command)
    {
        if (!_registry.TryGetValue(id, out var actor))
            throw new KeyNotFoundException($"Actor {id} not found.");

        actor.Post(new Envelope { Command = command });
    }

    /// <inheritdoc/>
    public async ValueTask<TResult> AskAsync<TResult>(Guid id, ICommand command)
    {
        if (!_registry.TryGetValue(id, out var actor))
            throw new KeyNotFoundException($"Actor {id} not found.");

        var tcs = new TaskCompletionSource<object?>();
        actor.Post(new Envelope { Command = command, Tcs = tcs });

        var result = await tcs.Task.ConfigureAwait(false);
        return (TResult)result!;
    }

    // ═══════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask StopAsync(Guid id)
    {
        if (!_registry.TryRemove(id, out var actor))
            return; // Idempotent: already stopped or never existed

        _logger?.Info($"Stopping actor {id}");
        await actor.StopAsync().ConfigureAwait(false);
        _logger?.Info($"Actor {id} stopped");
    }
}
