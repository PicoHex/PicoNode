namespace PicoNode;

public sealed class TcpNodeOptions
{
    public required IPEndPoint Endpoint { get; init; }
    public required ITcpConnectionHandler ConnectionHandler { get; init; }
    public Action<NodeFault>? FaultHandler { get; init; }
    public int MaxConnections { get; init; } = 1000;
    public int ReceiveSocketBufferSize { get; init; } = 4096;
    public int SendSocketBufferSize { get; init; } = 4096;
    public bool EnableAddressReuse { get; init; } = true;
    public bool EnableKeepAlive { get; init; }
    public bool NoDelay { get; init; } = true;
    public LingerOption LingerState { get; init; } = new(false, 0);
    public int Backlog { get; init; } = 128;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan IdleScanInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan AcceptFaultBackoff { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int? ReceivePipePauseThresholdBytes { get; init; }
    public int ReceivePipePauseThresholdMultiplier { get; init; } = 4;
    public bool EnableDualMode { get; init; }
    public SslServerAuthenticationOptions? SslOptions { get; init; }
}
