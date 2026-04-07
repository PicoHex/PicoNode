namespace PicoNode;

public sealed class TcpNode : INode, IAsyncDisposable
{
    private const string OperationStart = "tcp.start";
    private const string OperationStop = "tcp.stop.listener";
    private const string OperationAccept = "tcp.accept";
    private const string OperationAcceptCancelled = "tcp.accept.cancelled";
    private const string OperationRejectLimit = "tcp.reject.limit";
    private const string OperationRejectTracking = "tcp.reject.tracking";

    private readonly Socket _listener;
    private readonly SocketIoEventArgsPool _eventArgsPool;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, TcpConnection> _connections = new();
    private readonly Lock _stateLock = new();
    private Task? _acceptTask;
    private Task? _idleMonitorTask;
    private TaskCompletionSource<bool>? _drained;
    private volatile NodeState _state;
    private bool _disposed;

    public TcpNode(TcpNodeOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxConnections, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ReceiveSocketBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.SendSocketBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Backlog, 0);
        if (options.IdleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.IdleTimeout));
        }

        if (options.AcceptFaultBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.AcceptFaultBackoff));
        }

        if (options.DrainTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.DrainTimeout));
        }

        _listener = new Socket(options.Endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = options.NoDelay,
            LingerState = options.LingerState,
            ReceiveBufferSize = options.ReceiveSocketBufferSize,
            SendBufferSize = options.SendSocketBufferSize,
        };
        _eventArgsPool = new SocketIoEventArgsPool();

        if (options.Endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            _listener.DualMode = options.EnableDualMode;
        }

        _listener.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            options.EnableAddressReuse
        );
        _listener.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.KeepAlive,
            options.EnableKeepAlive
        );
        _state = NodeState.Created;
    }

    internal TcpNodeOptions Options { get; }

    internal SocketIoEventArgsPool EventArgsPool => _eventArgsPool;

    public EndPoint LocalEndPoint => _listener.LocalEndPoint ?? Options.Endpoint;

    public NodeState State => _state;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_state != NodeState.Created)
            {
                throw new InvalidOperationException("Node can only be started once.");
            }

            _state = NodeState.Starting;
        }

        try
        {
            _listener.Bind(Options.Endpoint);
            _listener.Listen(Options.Backlog);
            _acceptTask = AcceptLoopAsync();
            _idleMonitorTask = MonitorIdleConnectionsAsync();
            _state = NodeState.Running;
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _state = NodeState.Stopped;
            ReportFault(NodeFaultCode.StartFailed, OperationStart, ex);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_state is NodeState.Stopped or NodeState.Disposed or NodeState.Created)
        {
            return;
        }

        using var stopCts =
            Options.DrainTimeout > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
        if (stopCts is not null)
        {
            stopCts.CancelAfter(Options.DrainTimeout);
            cancellationToken = stopCts.Token;
        }

        TaskCompletionSource<bool>? drained;
        lock (_stateLock)
        {
            if (_state is NodeState.Stopped or NodeState.Disposed or NodeState.Created)
            {
                return;
            }

            _state = NodeState.Stopping;
            drained = _connections.IsEmpty
                ? null
                : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _drained = drained;
        }

        try
        {
            _cts.Cancel();

            try
            {
                _listener.Close();
            }
            catch (Exception ex)
            {
                ReportFault(NodeFaultCode.StopFailed, OperationStop, ex);
            }

            if (_acceptTask is not null)
            {
                try
                {
                    await _acceptTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) { }
            }

            if (_idleMonitorTask is not null)
            {
                try
                {
                    await _idleMonitorTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) { }
            }

            foreach (var connection in _connections.Values)
            {
                connection.Close(TcpCloseReason.NodeStopping);
            }

            if (drained is not null)
            {
                await drained.Task.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _drained = null;
                if (_state != NodeState.Disposed)
                {
                    _state = NodeState.Stopped;
                }
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        var acceptArgs = _eventArgsPool.Rent();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var acceptResult = await acceptArgs.AcceptAsync(_listener);
                    if (acceptResult.SocketError != SocketError.Success)
                    {
                        throw new SocketException((int)acceptResult.SocketError);
                    }

                    var socket =
                        acceptArgs.AcceptSocket
                        ?? throw new SocketException((int)SocketError.NotSocket);
                    acceptArgs.AcceptSocket = null;
                    await ProcessAcceptedSocketAsync(socket);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex) when (_cts.IsCancellationRequested)
                {
                    ReportFault(NodeFaultCode.AcceptFailed, OperationAcceptCancelled, ex);
                    break;
                }
                catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ReportFault(NodeFaultCode.AcceptFailed, OperationAccept, ex);
                    if (Options.AcceptFaultBackoff <= TimeSpan.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        await Task.Delay(Options.AcceptFaultBackoff, _cts.Token);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            _eventArgsPool.Return(acceptArgs);
        }
    }

    private async Task ProcessAcceptedSocketAsync(Socket socket)
    {
        socket.NoDelay = Options.NoDelay;
        socket.LingerState = Options.LingerState;

        if (_connections.Count >= Options.MaxConnections)
        {
            RejectAcceptedSocket(socket, NodeFaultCode.SessionRejected, OperationRejectLimit);
            return;
        }

        var connection = new TcpConnection(this, socket);
        if (!TryTrackConnection(connection))
        {
            ReportFault(NodeFaultCode.SessionRejected, OperationRejectTracking);
            await connection.DisposeAsync();
            return;
        }

        _ = connection.RunAsync(Options.ConnectionHandler);
    }

    private void RejectAcceptedSocket(Socket socket, NodeFaultCode code, string operation)
    {
        ReportFault(code, operation);
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch { }

        socket.Dispose();
    }

    private async Task MonitorIdleConnectionsAsync()
    {
        if (Options.IdleTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var interval =
            Options.IdleTimeout < TimeSpan.FromSeconds(1)
                ? Options.IdleTimeout
                : TimeSpan.FromSeconds(1);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(interval, _cts.Token);

                foreach (var connection in _connections.Values)
                {
                    if (connection.IsIdle(Options.IdleTimeout))
                    {
                        connection.Close(TcpCloseReason.IdleTimeout);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    private bool TryTrackConnection(TcpConnection connection)
    {
        lock (_stateLock)
        {
            if (_state is NodeState.Stopping or NodeState.Stopped or NodeState.Disposed)
            {
                return false;
            }

            return _connections.TryAdd(connection.Id, connection);
        }
    }

    internal void OnConnectionClosed(TcpConnection connection)
    {
        lock (_stateLock)
        {
            if (_connections.TryRemove(connection.Id, out _) && _connections.IsEmpty)
            {
                _drained?.TrySetResult(true);
            }
        }
    }

    internal void ReportFault(NodeFaultCode code, string operation, Exception? exception = null) =>
        NodeHelper.ReportFault(Options.FaultHandler, code, operation, exception);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Exception? stopException = null;

        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            stopException = ex;
        }
        finally
        {
            _cts.Dispose();
            _listener.Dispose();
            _eventArgsPool.Dispose();
            _state = NodeState.Disposed;
            GC.SuppressFinalize(this);
        }

        if (stopException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopException).Throw();
        }
    }
}
