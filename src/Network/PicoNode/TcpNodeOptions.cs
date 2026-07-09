namespace PicoNode;

public sealed class TcpNodeOptions
{
    public required IPEndPoint Endpoint { get; init; }
    public ITcpConnectionHandler ConnectionHandler { get; init; } = null!;
    public ILogger? Logger { get; init; }
    public ICfgRoot? Config { get; init; }
    public int MaxConnections { get; set; } = 1000;
    public int ReceiveSocketBufferSize { get; set; } = 4096;
    public int SendSocketBufferSize { get; set; } = 4096;
    public bool EnableAddressReuse { get; set; } = true;
    public bool EnableKeepAlive { get; set; }
    public bool NoDelay { get; set; } = true;
    public LingerOption LingerState { get; init; } = new(false, 0);
    public int Backlog { get; set; } = 128;
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan IdleScanInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan AcceptFaultBackoff { get; set; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int? ReceivePipePauseThresholdBytes { get; set; }
    public int ReceivePipePauseThresholdMultiplier { get; set; } = 4;
    public bool EnableDualMode { get; init; }
    public SslServerAuthenticationOptions? SslOptions { get; init; }
}
