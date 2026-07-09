namespace PicoNode;

public sealed class UdpDatagramContext : IUdpDatagramContext
{
    private readonly UdpNode _node;

    internal UdpDatagramContext(UdpNode node, long connectionId, EndPoint remoteEndPoint)
    {
        _node = node;
        ConnectionId = connectionId;
        RemoteEndPoint = remoteEndPoint;
    }

    public long ConnectionId { get; }

    public EndPoint RemoteEndPoint { get; }

    public Task SendAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken = default
    ) => _node.SendAsync(datagram, (IPEndPoint)RemoteEndPoint, cancellationToken);
}
