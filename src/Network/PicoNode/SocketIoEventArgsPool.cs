namespace PicoNode;

internal sealed class SocketIoEventArgsPool : IDisposable
{
    private readonly ConcurrentBag<SocketIoEventArgs> _pool = new();
    private int _disposed;

    public SocketIoEventArgsPool() { }

    public SocketIoEventArgs Rent()
    {
        return RentCore();
    }

    public void Return(SocketIoEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            DisposeEventArgs(eventArgs);
            return;
        }

        if (eventArgs.Buffer is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        eventArgs.Reset();
        _pool.Add(eventArgs);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_pool.TryTake(out var eventArgs))
        {
            DisposeEventArgs(eventArgs);
        }
    }

    private SocketIoEventArgs RentCore()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_pool.TryTake(out var eventArgs))
        {
            return eventArgs;
        }

        return new SocketIoEventArgs();
    }

    private static void DisposeEventArgs(SocketIoEventArgs eventArgs)
    {
        if (eventArgs.Buffer is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        eventArgs.Reset();
        eventArgs.Dispose();
    }
}
