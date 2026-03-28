namespace Pico.Node;

public sealed class UdpDatagramContext : IUdpDatagramContext
{
    private readonly UdpNode _node;

    internal UdpDatagramContext(UdpNode node, IPEndPoint remoteEndPoint)
    {
        _node = node;
        RemoteEndPoint = remoteEndPoint;
    }

    public IPEndPoint RemoteEndPoint { get; }

    public Task SendAsync(ArraySegment<byte> datagram, CancellationToken cancellationToken = default)
        => _node.SendAsync(datagram, RemoteEndPoint, cancellationToken);
}
