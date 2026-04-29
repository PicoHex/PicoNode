namespace PicoNode;

internal sealed class TcpConnection : IAsyncDisposable
{
    private const string OperationSend = "tcp.send";
    private const int MinimumReceiveBufferSize = 512;
    private const int DefaultReceivePipePauseThresholdMultiplier = 4;

    private readonly TcpNode _node;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly SendPath _sendPath;
    private readonly TcpConnectionLifecycle _lifecycle;
    private readonly TcpConnectionReceiveLoop _receiveLoop;
    private readonly int _receiveBufferSize;

    public TcpConnection(TcpNode node, Socket socket, Stream? stream = null)
    {
        _node = node;
        _receiveBufferSize = Math.Max(
            node.Options.ReceiveSocketBufferSize,
            MinimumReceiveBufferSize
        );
        _pipe = new Pipe(CreatePipeOptions(node.Options, _receiveBufferSize));
        _sendPath = new SendPath(socket, stream);
        var context = new TcpConnectionContext(this);
        _receiveLoop = new TcpConnectionReceiveLoop(
            socket,
            stream,
            _pipe,
            node,
            _receiveBufferSize,
            Touch
        );
        _lifecycle = new TcpConnectionLifecycle(
            node,
            this,
            socket,
            stream,
            _pipe,
            _sendLock,
            _cts,
            context,
            _receiveLoop
        );
        Id = Interlocked.Increment(ref _nextId);
        RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint!;
        ConnectedAtUtc = DateTimeOffset.UtcNow;
        _lastActivityTicks = ConnectedAtUtc.UtcTicks;
    }

    private static long _nextId;
    private long _lastActivityTicks;

    public long Id { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public DateTimeOffset ConnectedAtUtc { get; }

    public DateTimeOffset LastActivityUtc =>
        new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public Task RunAsync(ITcpConnectionHandler handler) => _lifecycle.RunAsync(handler);

    public Task SendAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_lifecycle.IsCloseInitiated)
        {
            throw new InvalidOperationException("Connection is closed.");
        }

        if (buffer.Length == 0)
        {
            return Task.CompletedTask;
        }

        return SendCoreAsync(buffer, cancellationToken);
    }

    private async Task SendCoreAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_lifecycle.IsCloseInitiated)
            {
                throw new InvalidOperationException("Connection is closed.");
            }

            if (buffer.Length == 0)
            {
                return;
            }

            await _sendPath.SendSequenceAsync(buffer, cancellationToken);

            _node.RecordBytesSent(buffer.Length);
            Touch();
        }
        catch (SocketException ex)
        {
            _node.ReportFault(NodeFaultCode.SendFailed, OperationSend, ex);
            _ = _lifecycle.CloseCoreAsync(
                TcpCloseReason.SendFault,
                ex,
                _node.Options.ConnectionHandler
            );
            throw;
        }
        catch (IOException ex)
        {
            _node.ReportFault(NodeFaultCode.SendFailed, OperationSend, ex);
            _ = _lifecycle.CloseCoreAsync(
                TcpCloseReason.SendFault,
                ex,
                _node.Options.ConnectionHandler
            );
            throw;
        }
        catch (ObjectDisposedException) when (_lifecycle.IsCloseInitiated)
        {
            throw new InvalidOperationException("Connection is closed.");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Close()
    {
        _ = _lifecycle.CloseCoreAsync(
            TcpCloseReason.LocalClose,
            null,
            _node.Options.ConnectionHandler
        );
    }

    public void Close(TcpCloseReason reason)
    {
        _ = _lifecycle.CloseCoreAsync(reason, null, _node.Options.ConnectionHandler);
    }

    internal bool IsIdle(TimeSpan idleTimeout)
    {
        if (idleTimeout <= TimeSpan.Zero)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - LastActivityUtc >= idleTimeout;
    }

    private void Touch()
    {
        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public ValueTask DisposeAsync() => _lifecycle.DisposeAsync();

    private static PipeOptions CreatePipeOptions(TcpNodeOptions options, int receiveBufferSize)
    {
        var pauseThreshold =
            options.ReceivePipePauseThresholdBytes
            ?? checked(receiveBufferSize * DefaultReceivePipePauseThresholdMultiplier);
        return new PipeOptions(
            pauseWriterThreshold: pauseThreshold,
            resumeWriterThreshold: pauseThreshold / 2,
            minimumSegmentSize: receiveBufferSize,
            useSynchronizationContext: false
        );
    }
}
