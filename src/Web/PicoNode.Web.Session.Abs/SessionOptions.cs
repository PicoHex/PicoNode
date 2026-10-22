namespace PicoNode.Web.Session.Abs;

public sealed class SessionOptions
{
    /// <summary>Idle timeout. A session untouched for longer is no longer loadable.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(20);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hard ceiling on live sessions held by a store. Reaching it evicts the least
    /// recently used sessions, so an unauthenticated caller minting a session per
    /// request (a cookie-less request that dirties the session is enough) cannot grow
    /// the store without bound. Defaults to 10,000.
    /// </summary>
    public int MaxSessions { get; init; } = 10_000;

    /// <summary>
    /// Ceiling on the accounted payload of a single session, in bytes (the sum of its
    /// stored value lengths). Exceeding it makes the write throw rather than silently
    /// truncate. Defaults to 256 KiB.
    /// </summary>
    public int MaxBytesPerSession { get; init; } = 256 * 1024;
}
