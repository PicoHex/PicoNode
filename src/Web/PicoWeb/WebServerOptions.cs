namespace PicoWeb;

public sealed class WebServerOptions
{
    public required IPEndPoint Endpoint { get; init; }

    public SslServerAuthenticationOptions? SslOptions { get; init; }

    public bool EnableDualMode { get; init; } = true;

    public ILogger? Logger { get; init; }

    /// <summary>Maximum concurrent connections. Default: 1000.</summary>
    public int MaxConnections { get; init; } = 1000;

    /// <summary>Receive socket buffer size in bytes. Default: 4096.</summary>
    public int ReceiveSocketBufferSize { get; init; } = 4096;

    /// <summary>Send socket buffer size in bytes. Default: 4096.</summary>
    public int SendSocketBufferSize { get; init; } = 4096;

    /// <summary>Disables Nagle algorithm when true. Default: true.</summary>
    public bool NoDelay { get; init; } = true;

    /// <summary>Idle connection timeout. Default: 2 minutes.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Graceful shutdown drain timeout. Default: 5 seconds. Zero means "no deadline at all"; a value
    /// above the longest timeout a timer-backed wait accepts (~49.7 days) is capped at it, so the drain
    /// still runs instead of throwing out of the stop.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Backoff delay between accept retries after a fault. Default: 50ms. A delay of zero — or one a
    /// config reload makes non-positive — is raised to a 1 ms floor when the accept loop reads it, so
    /// this option always means "how long to pause", never "do not pause".
    /// </summary>
    public TimeSpan AcceptFaultBackoff { get; init; } = TimeSpan.FromMilliseconds(50);
}
