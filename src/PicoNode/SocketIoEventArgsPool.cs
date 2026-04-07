namespace PicoNode;

internal sealed class SocketIoEventArgsPool : IDisposable
{
    private readonly ConcurrentBag<SocketIoEventArgs> _pool = new();
    private bool _disposed;

    public SocketIoEventArgsPool() { }

    public SocketIoEventArgs Rent()
    {
        var eventArgs = RentCore();
        eventArgs.AcceptSocket = null;
        eventArgs.SetBuffer(null, 0, 0);
        return eventArgs;
    }

    public void Return(SocketIoEventArgs eventArgs)
    {
        if (_disposed)
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_pool.TryTake(out var eventArgs))
        {
            DisposeEventArgs(eventArgs);
        }
    }

    private SocketIoEventArgs RentCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
