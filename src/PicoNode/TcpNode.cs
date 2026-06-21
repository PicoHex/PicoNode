namespace PicoNode;

public sealed class TcpNode : INode
{
    private const string OperationStart = "tcp.start";
    private const string OperationStop = "tcp.stop.listener";
    private const string OperationAccept = "tcp.accept";
    private const string OperationAcceptCancelled = "tcp.accept.cancelled";
    private const string OperationRejectLimit = "tcp.reject.limit";
    private const string OperationRejectTracking = "tcp.reject.tracking";
    private const string OperationTls = "tcp.tls";

    private readonly Socket _listener;
    private readonly SocketIoEventArgsPool _eventArgsPool;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Cancellation token source for the config reload loop.
    /// NOT cancelled during <see cref="StopAsync"/> — the config loop survives
    /// until <see cref="DisposeAsync"/> cancels it. This allows the last config
    /// reload to complete during the stop→dispose window.
    /// </summary>
    private readonly CancellationTokenSource _configCts = new();
    private readonly ConcurrentDictionary<long, TcpConnection> _connections = new();
    private readonly Lock _stateLock = new();
    private Task? _acceptTask;
    private Task? _idleMonitorTask;

    /// <summary>
    /// Background task for configuration hot-reload. Started in <see cref="StartAsync"/>
    /// and survives <see cref="StopAsync"/>. Only cancelled in <see cref="DisposeAsync"/>
    /// to ensure in-flight reloads complete gracefully.
    /// </summary>
    private Task? _configReloadTask;
    private TaskCompletionSource<bool>? _drained;
    private volatile NodeState _state;
    private int _disposed;
    private long _totalAccepted;
    private long _totalRejected;
    private long _totalClosed;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _prevAccepted;
    private long _prevSent;
    private long _prevReceived;
    private DateTime _metricsTime = DateTime.UtcNow;

    public TcpNode(TcpNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ConnectionHandler);
        Options = options;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxConnections, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ReceiveSocketBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.SendSocketBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Backlog, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.IdleScanInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.AcceptFaultBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DrainTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.ReceivePipePauseThresholdBytes ?? int.MaxValue,
            0
        );

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

    public EndPoint LocalEndPoint => _listener.LocalEndPoint ?? Options.Endpoint;

    public NodeState State => _state;

    public TcpNodeMetrics GetMetrics()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _metricsTime).TotalSeconds;

        var accepted = Interlocked.Read(ref _totalAccepted);
        var sent = Interlocked.Read(ref _totalBytesSent);
        var received = Interlocked.Read(ref _totalBytesReceived);

        var acceptRate = elapsed > 0 ? (accepted - _prevAccepted) / elapsed : 0;
        var sentRate = elapsed > 0 ? (sent - _prevSent) / elapsed : 0;
        var recvRate = elapsed > 0 ? (received - _prevReceived) / elapsed : 0;

        _prevAccepted = accepted;
        _prevSent = sent;
        _prevReceived = received;
        _metricsTime = now;

        return new TcpNodeMetrics(
            accepted,
            Interlocked.Read(ref _totalRejected),
            Interlocked.Read(ref _totalClosed),
            _connections.Count,
            sent,
            received,
            acceptRate,
            sentRate,
            recvRate
        );
    }

    internal void RecordBytesSent(long count) => Interlocked.Add(ref _totalBytesSent, count);

    internal void RecordBytesReceived(long count) =>
        Interlocked.Add(ref _totalBytesReceived, count);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

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

            if (Options.Config is not null)
            {
                _configReloadTask = ConfigReloadLoopAsync(Options.Config, Options);
            }

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

        var stopCompleted = false;

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
                : new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
            _drained = drained;
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);

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
                    await _acceptTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                { /* expected during shutdown �?accept task cancelled */
                }
            }

            if (_idleMonitorTask is not null)
            {
                try
                {
                    await _idleMonitorTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                { /* expected during shutdown �?idle monitor task cancelled */
                }
            }

            foreach (var connection in _connections.Values)
            {
                connection.Close(TcpCloseReason.NodeStopping);
            }

            if (drained is not null)
            {
                await drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            stopCompleted = true;
        }
        finally
        {
            lock (_stateLock)
            {
                if (stopCompleted)
                {
                    _drained = null;
                }

                if (_state != NodeState.Disposed && stopCompleted)
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
                    var acceptResult = await acceptArgs
                        .AcceptAsync(_listener)
                        .ConfigureAwait(false);
                    if (acceptResult.SocketError != SocketError.Success)
                    {
                        throw new SocketException((int)acceptResult.SocketError);
                    }

                    var socket =
                        acceptArgs.AcceptSocket
                        ?? throw new SocketException((int)SocketError.NotSocket);
                    acceptArgs.AcceptSocket = null;
                    await ProcessAcceptedSocketAsync(socket).ConfigureAwait(false);
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
                        await Task.Delay(Options.AcceptFaultBackoff, _cts.Token)
                            .ConfigureAwait(false);
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

    private Task ProcessAcceptedSocketAsync(Socket socket)
    {
        socket.NoDelay = Options.NoDelay;
        socket.LingerState = Options.LingerState;

        if (_connections.Count >= Options.MaxConnections)
        {
            Interlocked.Increment(ref _totalRejected);
            RejectAcceptedSocket(socket, NodeFaultCode.SessionRejected, OperationRejectLimit);
            return Task.CompletedTask;
        }

        if (Options.SslOptions is not null)
        {
            // Offload TLS handshake to avoid blocking the accept loop thread.
            _ = Task.Run(async () =>
            {
                try
                {
                    await TrackAndRunAsync(socket).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ReportFault(NodeFaultCode.TlsFailed, OperationTls, ex);
                }
            });
            return Task.CompletedTask;
        }

        return TrackAndRunAsync(socket);
    }

    private async Task TrackAndRunAsync(Socket socket, Stream? stream = null)
    {
        if (stream is null && Options.SslOptions is not null)
        {
            stream = await NegotiateTlsAsync(socket).ConfigureAwait(false);
            if (stream is null)
            {
                return;
            }
        }

        var connection = new TcpConnection(this, socket, stream);
        if (!TryTrackConnection(connection))
        {
            ReportFault(NodeFaultCode.SessionRejected, OperationRejectTracking);
            Interlocked.Increment(ref _totalRejected);
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref _totalAccepted);
        _ = connection.RunAsync(Options.ConnectionHandler);
    }

    private async Task<SslStream?> NegotiateTlsAsync(Socket socket)
    {
        var networkStream = new NetworkStream(socket, ownsSocket: false);
        var traceStream = new TlsTraceStream(networkStream, "SRV");
        var sslStream = new SslStream(traceStream, leaveInnerStreamOpen: false);
        try
        {
            await sslStream
                .AuthenticateAsServerAsync(Options.SslOptions!, _cts.Token)
                .ConfigureAwait(false);
            Options.Logger?.Log(
                LogLevel.Debug,
                $"[TLS] SRV done: {sslStream.SslProtocol} ALPN={sslStream.NegotiatedApplicationProtocol}",
                null
            );
            return sslStream;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            socket.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.TlsFailed, OperationTls, ex);
            await sslStream.DisposeAsync().ConfigureAwait(false);
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception socketEx)
            {
                Options.Logger?.Log(
                    LogLevel.Debug,
                    new EventId(0),
                    "Socket shutdown during TLS teardown failed",
                    socketEx
                );
            }
            socket.Dispose();
            return null;
        }
    }

    private void RejectAcceptedSocket(Socket socket, NodeFaultCode code, string operation)
    {
        ReportFault(code, operation);
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex)
        {
            Options.Logger?.Log(
                LogLevel.Debug,
                new EventId(0),
                "Socket shutdown during reject cleanup failed",
                ex
            );
        }

        socket.Dispose();
    }

    private async Task MonitorIdleConnectionsAsync()
    {
        if (Options.IdleTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var interval =
            Options.IdleTimeout < Options.IdleScanInterval
                ? Options.IdleTimeout
                : Options.IdleScanInterval;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(interval, _cts.Token).ConfigureAwait(false);

                foreach (var connection in _connections.Values)
                {
                    if (connection.IsIdle(Options.IdleTimeout))
                    {
                        connection.Close(TcpCloseReason.IdleTimeout);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        { /* expected during shutdown �?idle monitor exits via cancellation */
        }
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
        Interlocked.Increment(ref _totalClosed);
        lock (_stateLock)
        {
            if (_connections.TryRemove(connection.Id, out _) && _connections.IsEmpty)
            {
                _drained?.TrySetResult(true);
            }
        }
    }

    public event Action<NodeFault>? OnFault;

    internal void ReportFault(NodeFaultCode code, string operation, Exception? exception = null)
    {
        var fault = new NodeFault(code, operation, exception);
        NodeHelper.ReportFault(Options.Logger, code, operation, exception);
        OnFault?.Invoke(fault);
    }

    /// <summary>
    /// Background loop for configuration hot-reload.
    /// Survives <see cref="StopAsync"/> and is only stopped by
    /// <see cref="DisposeAsync"/> cancelling <see cref="_configCts"/>.
    /// This ensures the last reload can complete during the stop→dispose
    /// window without blocking shutdown.
    /// </summary>
    private async Task ConfigReloadLoopAsync(ICfgRoot config, TcpNodeOptions options)
    {
        try
        {
            while (!_configCts.IsCancellationRequested)
            {
                await config.WaitForChangeAsync(_configCts.Token).ConfigureAwait(false);

                if (_configCts.IsCancellationRequested)
                    break;

                var reloaded = await config.ReloadAsync(_configCts.Token).ConfigureAwait(false);
                if (reloaded)
                {
                    ApplyConfigReload(config, options);
                }
            }
        }
        catch (OperationCanceledException) when (_configCts.IsCancellationRequested)
        { /* expected during shutdown */
        }
        catch (Exception ex)
        {
            Options.Logger?.Log(
                LogLevel.Warning,
                new EventId(0),
                "Config reload failed (best-effort, continuing)",
                ex
            );
        }
    }

    private static void ApplyConfigReload(ICfg config, TcpNodeOptions options)
    {
        if (config.TryGetValue("MaxConnections", out var v) && int.TryParse(v, out var val))
            options.MaxConnections = val;
        if (config.TryGetValue("ReceiveSocketBufferSize", out v) && int.TryParse(v, out val))
            options.ReceiveSocketBufferSize = val;
        if (config.TryGetValue("SendSocketBufferSize", out v) && int.TryParse(v, out val))
            options.SendSocketBufferSize = val;
        if (config.TryGetValue("EnableAddressReuse", out v) && bool.TryParse(v, out var b))
            options.EnableAddressReuse = b;
        if (config.TryGetValue("EnableKeepAlive", out v) && bool.TryParse(v, out b))
            options.EnableKeepAlive = b;
        if (config.TryGetValue("NoDelay", out v) && bool.TryParse(v, out b))
            options.NoDelay = b;
        if (config.TryGetValue("Backlog", out v) && int.TryParse(v, out val))
            options.Backlog = val;
        if (config.TryGetValue("IdleTimeout", out v) && TimeSpan.TryParse(v, out var ts))
            options.IdleTimeout = ts;
        if (config.TryGetValue("IdleScanInterval", out v) && TimeSpan.TryParse(v, out ts))
            options.IdleScanInterval = ts;
        if (config.TryGetValue("AcceptFaultBackoff", out v) && TimeSpan.TryParse(v, out ts))
            options.AcceptFaultBackoff = ts;
        if (config.TryGetValue("DrainTimeout", out v) && TimeSpan.TryParse(v, out ts))
            options.DrainTimeout = ts;
        if (config.TryGetValue("ReceivePipePauseThresholdBytes", out v))
            options.ReceivePipePauseThresholdBytes = int.TryParse(v, out val) ? val : null;
        if (
            config.TryGetValue("ReceivePipePauseThresholdMultiplier", out v)
            && int.TryParse(v, out val)
        )
            options.ReceivePipePauseThresholdMultiplier = val;
        // Endpoint, EnableDualMode, ConnectionHandler, LingerState, Logger, SslOptions are code-only
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Exception? stopException = null;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopException = ex;
        }
        finally
        {
            _configCts.Cancel();
            _cts.Dispose();
            _listener.Dispose();
            _eventArgsPool.Dispose();
            _state = NodeState.Disposed;
        }

        if (_configReloadTask is not null)
        {
            try
            {
                await _configReloadTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Options.Logger?.Log(
                    LogLevel.Debug,
                    new EventId(0),
                    "Config reload task completion failed (best-effort)",
                    ex
                );
            }
        }

        _configCts.Dispose();

        if (stopException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopException).Throw();
        }
    }

    /// <summary>Diagnostic stream that logs raw bytes at TLS record level.</summary>
    internal sealed class TlsTraceStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _label;
        private int _rid,
            _wid;

        public TlsTraceStream(Stream inner, string label)
        {
            _inner = inner;
            _label = label;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position
        {
            get => 0;
            set { }
        }

        public override void Flush() => _inner.Flush();

        public override long Seek(long o, SeekOrigin org) => 0;

        public override void SetLength(long v) { }

        public override int Read(byte[] b, int o, int c) => 0;

        public override void Write(byte[] b, int o, int c) { }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buf,
            CancellationToken ct = default
        )
        {
            var r = await _inner.ReadAsync(buf, ct);
            if (r > 0)
                Console.WriteLine(
                    "[{0}] TLS← #{1}: {2}B | {3}",
                    _label,
                    ++_rid,
                    r,
                    BitConverter
                        .ToString(buf.Span.Slice(0, Math.Min(r, 50)).ToArray())
                        .Replace('-', ' ')
                );
            return r;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buf,
            CancellationToken ct = default
        )
        {
            Console.WriteLine(
                "[{0}] TLS→ #{1}: {2}B | {3}",
                _label,
                ++_wid,
                buf.Length,
                BitConverter
                    .ToString(buf.Span.Slice(0, Math.Min(buf.Length, 50)).ToArray())
                    .Replace('-', ' ')
            );
            await _inner.WriteAsync(buf, ct);
        }
    }
}
