namespace PicoNode;

public sealed class UdpNodeOptions
{
    public const int DefaultReceiveDatagramBufferSize = 2048;
    public static readonly TimeSpan DefaultReceiveFaultBackoff = TimeSpan.FromMilliseconds(10);

    public required IPEndPoint Endpoint { get; init; }
    public IUdpDatagramHandler DatagramHandler { get; init; } = null!;
    public ILogger? Logger { get; init; }
    public ICfgRoot? Config { get; init; }
    public int ReceiveSocketBufferSize { get; set; } = 1 << 20;
    public int SendSocketBufferSize { get; set; } = 1 << 20;
    public int ReceiveDatagramBufferSize { get; set; } = DefaultReceiveDatagramBufferSize;
    public int DispatchWorkerCount { get; init; } = 1;
    public int DatagramQueueCapacity { get; init; } = 1024;
    public bool EnableBroadcast { get; set; } = true;
    public UdpOverflowMode QueueOverflowMode { get; set; } = UdpOverflowMode.DropNewest;
    public TimeSpan ReceiveFaultBackoff { get; set; } = DefaultReceiveFaultBackoff;
    public IPAddress? MulticastGroup { get; init; }
    public int MulticastTtl { get; set; } = 1;
    public bool MulticastLoopback { get; set; } = true;
}
