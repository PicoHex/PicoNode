namespace PicoNode.Web.Session.Abs;

public sealed class SessionOptions
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(20);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);
}
