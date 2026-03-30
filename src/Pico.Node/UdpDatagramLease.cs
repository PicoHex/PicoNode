namespace Pico.Node;

internal sealed class UdpDatagramLease : IDisposable
{
    private bool _disposed;

    public UdpDatagramLease(byte[] buffer, int count, IPEndPoint remoteEndPoint)
    {
        Buffer = buffer;
        Count = count;
        RemoteEndPoint = remoteEndPoint;
    }

    public byte[] Buffer { get; }

    public int Count { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public ArraySegment<byte> Datagram => new(Buffer, 0, Count);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(Buffer, clearArray: false);
    }
}
