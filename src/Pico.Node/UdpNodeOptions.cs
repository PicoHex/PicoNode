namespace Pico.Node;

public sealed class UdpNodeOptions
{
    public const int DefaultReceiveDatagramBufferSize = 2048;

    public required IPEndPoint Endpoint { get; init; }
    public required IUdpDatagramHandler DatagramHandler { get; init; }
    public Action<NodeFault>? FaultHandler { get; init; }
    public int ReceiveSocketBufferSize { get; init; } = 1 << 20;
    public int SendSocketBufferSize { get; init; } = 1 << 20;
    public int ReceiveDatagramBufferSize { get; init; } = DefaultReceiveDatagramBufferSize;
    public int DispatchWorkerCount { get; init; } = 1;
    public int DatagramQueueCapacity { get; init; } = 1024;
    public bool EnableBroadcast { get; init; } = true;
    public UdpOverflowMode QueueOverflowMode { get; init; } = UdpOverflowMode.DropNewest;
}
