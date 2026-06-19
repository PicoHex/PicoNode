namespace PicoNode.Abs;

/// <summary>Handles incoming UDP datagrams dispatched by a <c>UdpNode</c>.</summary>
public interface IUdpDatagramHandler
{
    /// <summary>Called when a datagram is received. The <paramref name="datagram"/> buffer is valid only during this call; copy data if it must outlive the method.</summary>
    ValueTask OnDatagramAsync(
        IUdpDatagramContext context,
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken
    );
}
