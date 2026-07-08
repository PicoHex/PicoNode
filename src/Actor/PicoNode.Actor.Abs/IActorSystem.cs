namespace PicoNode.Actor.Abs;

/// <summary>
/// Actor runtime. Entry point for creating, locating, messaging, and stopping actors.
/// Registered as a PicoDI Singleton.
/// </summary>
public interface IActorSystem
{
    /// <summary>Create an actor and start its mailbox. ID is framework-generated (UUID v7).</summary>
    ValueTask<T> CreateAsync<T>(ActorOptions? options = null) where T : IActor;

    /// <summary>Get an actor proxy by UUID v7. Returns null if not found.</summary>
    T? Get<T>(Guid id) where T : IActor;

    /// <summary>Send a fire-and-forget command to an actor.</summary>
    void Send(Guid id, ICommand command);

    /// <summary>Send a command and await the result.</summary>
    ValueTask<TResult> AskAsync<TResult>(Guid id, ICommand command);

    /// <summary>Stop an actor. Waits for the current message to complete, discards remaining queue.</summary>
    ValueTask StopAsync(Guid id);
}
