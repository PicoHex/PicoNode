namespace PicoAgent;

using System.Net;

public sealed class AgentServerOptions
{
    public required IPEndPoint Endpoint { get; init; }

    public SslServerAuthenticationOptions? SslOptions { get; init; }

    public bool EnableDualMode { get; init; } = true;

    public ILogger? Logger { get; init; }

    public int MaxConnections { get; init; } = 1000;

    public int ReceiveSocketBufferSize { get; init; } = 4096;

    public int SendSocketBufferSize { get; init; } = 4096;

    public bool NoDelay { get; init; } = true;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan AcceptFaultBackoff { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Server header sent in HTTP responses. Default: "PicoAgent".
    /// </summary>
    public string ServerHeader { get; init; } = "PicoAgent";
}
