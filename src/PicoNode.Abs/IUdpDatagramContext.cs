namespace PicoNode.Abs;

public interface IUdpDatagramContext
{
    long ConnectionId { get; }

    EndPoint RemoteEndPoint { get; }

    Task SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default);
}
