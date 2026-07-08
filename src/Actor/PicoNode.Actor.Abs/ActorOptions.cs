namespace PicoNode.Actor.Abs;

/// <summary>Optional configuration when creating an actor.</summary>
public sealed class ActorOptions
{
    /// <summary>Mailbox capacity. 0 = unbounded.</summary>
    public int MailboxCapacity { get; init; }
}
