namespace PicoNode;

internal sealed class UdpDatagramLease(byte[] buffer, int count, EndPoint remoteEndPoint)
    : IDisposable
{
    private int _disposed;

    public byte[] Buffer { get; } = buffer;

    public int Count { get; } = count;

    public EndPoint RemoteEndPoint { get; } = remoteEndPoint;

    public ReadOnlyMemory<byte> Datagram => new ReadOnlyMemory<byte>(Buffer, 0, Count);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        ArrayPool<byte>.Shared.Return(Buffer, clearArray: false);
    }
}
