namespace PicoNode.Actor.Abs;

/// <summary>
/// Actor runtime. Entry point for creating, locating, messaging, and stopping actors.
/// Registered as a PicoDI Singleton.
/// </summary>
public interface IActorSystem
{
    /// <summary>
    /// Register a factory for creating a new actor from a creation command.
    /// The factory receives the framework-generated UUID v7 and the caller's command.
    /// </summary>
    void Register<T>(Func<Guid, ICommand, T> factory) where T : IActor;

    /// <summary>
    /// Create a new actor using the registered factory.
    /// ID is framework-generated (UUID v7). The command carries construction data.
    /// </summary>
    ValueTask<T> CreateAsync<T>(ICommand command) where T : IActor;

    /// <summary>
    /// Get an actor by UUID v7. Returns from memory if active;
    /// otherwise rebuilds from persisted events. Returns null if not found.
    /// Rebuild path calls new T(id) + ReplayEvents — bypasses the creation factory.
    /// </summary>
    ValueTask<T?> GetAsync<T>(Guid id) where T : IActor;

    /// <summary>Send a fire-and-forget command to an actor.</summary>
    void Send(Guid id, ICommand command);

    /// <summary>Send a command and await the result.</summary>
    ValueTask<TResult> AskAsync<TResult>(Guid id, ICommand command);

    /// <summary>Stop an actor. Waits for the current message to complete, discards remaining queue.</summary>
    ValueTask StopAsync(Guid id);
}
