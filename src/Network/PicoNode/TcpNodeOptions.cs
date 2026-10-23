namespace PicoNode;

public sealed class TcpNodeOptions
{
    public required IPEndPoint Endpoint { get; init; }
    public ITcpConnectionHandler ConnectionHandler { get; init; } = null!;

    /// <summary>Optional log sink for node diagnostics.</summary>
    /// <remarks>
    /// The only sink for a fault nothing else observes: a loop that outlives <c>DisposeAsync</c>'s
    /// bounded wait is reported as <see cref="NodeFaultCode.StopFailed"/> through this logger and
    /// <see cref="INode.OnFault"/>, and through nothing else — with neither wired the node simply
    /// retains the member that loop was using and returns.
    /// </remarks>
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

    /// <summary>
    /// Pause between two accept attempts after a failed accept. A value that would switch the pause
    /// off (zero, or negative through a config reload) is raised to a one-millisecond floor where the
    /// accept loop reads it, so this option always means "how long to pause", never "do not pause".
    /// </summary>
    public TimeSpan AcceptFaultBackoff { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Budget for draining in-flight connections during <see cref="INode.StopAsync"/>.
    /// <see cref="TimeSpan.Zero"/> means "no deadline at all". A positive budget above the longest
    /// timeout a timer-backed wait accepts (~49.7 days) is capped at it where the stop reads it
    /// (<c>DrainBudget.Effective</c>), because the deadline timer itself rejects anything larger. The
    /// wait for a closing connection's handler loop is derived from it — half of it, capped the same
    /// way (<c>HandlerLoopTimeoutFor</c>) — so the steps that follow that wait inside the same budget
    /// still have room.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int? ReceivePipePauseThresholdBytes { get; set; }
    public int ReceivePipePauseThresholdMultiplier { get; set; } = 4;
    public bool EnableDualMode { get; init; }
    public SslServerAuthenticationOptions? SslOptions { get; init; }
}
