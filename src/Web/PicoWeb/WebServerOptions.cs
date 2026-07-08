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

    /// <summary>Graceful shutdown drain timeout. Default: 5 seconds.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Backoff delay between accept retries after a fault. Default: 50ms.</summary>
    public TimeSpan AcceptFaultBackoff { get; init; } = TimeSpan.FromMilliseconds(50);
}
