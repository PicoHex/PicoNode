namespace PicoNode;

internal sealed class SocketIoEventArgs
    : SocketAsyncEventArgs,
        IValueTaskSource<SocketAsyncEventArgs>
{
    private ManualResetValueTaskSourceCore<SocketAsyncEventArgs> _source;
    private bool _pending;

    public SocketIoEventArgs()
    {
        _source.RunContinuationsAsynchronously = true;
        Completed += OnCompleted;
    }

    public ValueTask<SocketAsyncEventArgs> ReceiveAsync(Socket socket) =>
        ExecuteAsync(socket.ReceiveAsync);

    public ValueTask<SocketAsyncEventArgs> SendAsync(Socket socket) =>
        ExecuteAsync(socket.SendAsync);

    public ValueTask<SocketAsyncEventArgs> AcceptAsync(Socket socket) =>
        ExecuteAsync(socket.AcceptAsync);

    public void Reset()
    {
        AcceptSocket = null;
        DisconnectReuseSocket = false;
        RemoteEndPoint = null;
        UserToken = null;
        SetBuffer(null, 0, 0);
    }

    public SocketAsyncEventArgs GetResult(short token) => _source.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

    public void OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags
    ) => _source.OnCompleted(continuation, state, token, flags);

    private ValueTask<SocketAsyncEventArgs> ExecuteAsync(Func<SocketAsyncEventArgs, bool> start)
    {
        if (_pending)
        {
            throw new InvalidOperationException("Socket async operation is already pending.");
        }

        _pending = true;
        _source.Reset();

        try
        {
            if (!start(this))
            {
                _pending = false;
                return ValueTask.FromResult<SocketAsyncEventArgs>(this);
            }
        }
        catch
        {
            _pending = false;
            throw;
        }

        return new ValueTask<SocketAsyncEventArgs>(this, _source.Version);
    }

    private void OnCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        _pending = false;
        _source.SetResult(eventArgs);
    }
}
