namespace PicoNode.Abs;

public interface IUdpDatagramContext
{
    long ConnectionId { get; }

    IPEndPoint RemoteEndPoint { get; }

    Task SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default);
}
