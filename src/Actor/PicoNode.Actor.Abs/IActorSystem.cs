namespace PicoNode.Actor.Abs;

/// <summary>
/// Actor runtime. Entry point for creating, locating, messaging, and stopping actors.
/// Registered as a PicoDI Singleton.
/// </summary>
public interface IActorSystem
{
    /// <summary>
    /// Register a factory for creating a new actor from a creation command.
    /// The factory receives only the caller's command — the UUID v7 is generated internally
    /// by the ActorSystem and assigned to the actor's Id property after construction.
    /// </summary>
    void Register<T>(Func<ICommand, T> factory)
        where T : IActor;

    /// <summary>
    /// Create a new actor using the registered factory.
    /// 1. Calls the factory with the command to construct the actor (pure, no DI).
    /// 2. Assigns a UUID v7 Id.
    /// 3. Registers in the system.
    /// 4. Calls SignalReady() to release the consumption loop.
    /// </summary>
    ValueTask<T> CreateAsync<T>(ICommand command)
        where T : IActor;

    /// <summary>
    /// Get an actor by UUID v7. Returns from memory if active;
    /// otherwise rebuilds from persisted events. Returns null if not found.
    /// Rebuild path: new T() → assign Id → ReplayEvents → SignalReady.
    /// No factory involved — the public parameterless constructor is used.
    /// The new() constraint is AOT-safe (compiler generates direct call, zero reflection).
    /// </summary>
    ValueTask<T?> GetAsync<T>(Guid id)
        where T : IActor, new();

    /// <summary>Send a fire-and-forget command to an actor.</summary>
    void Send(Guid id, ICommand command);

    /// <summary>Send a command and await the result.</summary>
    ValueTask<TResult> AskAsync<TResult>(Guid id, ICommand command);

    /// <summary>Stop an actor. Waits for the current message to complete, discards remaining queue.</summary>
    ValueTask StopAsync(Guid id);
}
