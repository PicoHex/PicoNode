namespace PicoNode;

public sealed class UdpNodeOptions
{
    public const int DefaultReceiveDatagramBufferSize = 2048;
    public static readonly TimeSpan DefaultReceiveFaultBackoff = TimeSpan.FromMilliseconds(10);

    public required IPEndPoint Endpoint { get; init; }
    public IUdpDatagramHandler DatagramHandler { get; init; } = null!;

    /// <summary>Optional log sink for node diagnostics.</summary>
    /// <remarks>
    /// The only sink for a fault nothing else observes: a loop that outlives <c>DisposeAsync</c>'s
    /// bounded wait is reported as <see cref="NodeFaultCode.StopFailed"/> through this logger and
    /// <see cref="INode.OnFault"/>, and through nothing else — with neither wired the node simply
    /// retains the member that loop was using and returns.
    /// </remarks>
    public ILogger? Logger { get; init; }
    public ICfgRoot? Config { get; init; }
    public int ReceiveSocketBufferSize { get; set; } = 1 << 20;
    public int SendSocketBufferSize { get; set; } = 1 << 20;
    public int ReceiveDatagramBufferSize { get; set; } = DefaultReceiveDatagramBufferSize;
    public int DispatchWorkerCount { get; init; } = 1;
    public int DatagramQueueCapacity { get; init; } = 1024;
    public bool EnableBroadcast { get; set; } = true;
    public UdpOverflowMode QueueOverflowMode { get; set; } = UdpOverflowMode.DropNewest;

    /// <summary>
    /// Pause between two receive attempts after a failed receive. A value that would switch the pause
    /// off (zero, or negative through a config reload) is raised to a one-millisecond floor where the
    /// receive loop reads it, and a value above the longest legal timer delay is capped at it, so this
    /// option always means "how long to pause", never "do not pause".
    /// </summary>
    public TimeSpan ReceiveFaultBackoff { get; set; } = DefaultReceiveFaultBackoff;

    /// <summary>
    /// Budget for draining in-flight datagrams during <see cref="INode.StopAsync"/>.
    /// <see cref="TimeSpan.Zero"/> means "no deadline at all", and a value above the longest timeout a
    /// timer-backed wait accepts (~49.7 days) is capped at it where the stop reads it, because the
    /// deadline timer rejects anything larger. The datagram handlers keep their own lifetime until this
    /// budget is spent, so what is already queued is delivered rather than aborted.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public IPAddress? MulticastGroup { get; init; }
    public int MulticastTtl { get; set; } = 1;
    public bool MulticastLoopback { get; set; } = true;
}
