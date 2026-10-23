namespace PicoNode;

public sealed class TcpNode : INode
{
    private const string OperationStart = "tcp.start";
    private const string OperationStop = "tcp.stop.listener";
    private const string OperationStopSignal = "tcp.stop.signal";
    private const string OperationStopIdle = "tcp.stop.idle";
    private const string OperationAccept = "tcp.accept";
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
    private int _tlsHandshakesInFlight;
    private Task? _acceptTask;
    private Task? _idleMonitorTask;

    /// <summary>
    /// Background task for configuration hot-reload. Started in <see cref="StartAsync"/>
    /// and survives <see cref="StopAsync"/>. Only cancelled in <see cref="DisposeAsync"/>
    /// to ensure in-flight reloads complete gracefully.
    /// </summary>
    private Task? _configReloadTask;

    /// <summary>
    /// The one and only drain source for this node: created exactly once — under
    /// <see cref="_stateLock"/>, by the caller that performs the Running→Stopping
    /// transition — and signalled exactly once by <see cref="OnConnectionClosed"/> when the
    /// last tracked connection is removed. Every <see cref="StopAsync"/> caller awaits this
    /// very task, so a repeat or concurrent stop can never be stranded on a source nobody
    /// signals. It is never reset: the Stopping transition is one way, so a node is
    /// stopped at most once.
    /// </summary>
    private TaskCompletionSource? _drainSource;
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
    private long _acceptFaultBackoffs;
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
        // The rate snapshot (prev values + timestamp) is guarded so concurrent
        // callers cannot observe torn or backwards-moving rate state.
        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _metricsTime).TotalSeconds;

            var accepted = Interlocked.Read(ref _totalAccepted);
            var sent = Interlocked.Read(ref _totalBytesSent);
            var received = Interlocked.Read(ref _totalBytesReceived);

            double acceptRate = 0,
                sentRate = 0,
                recvRate = 0;

            // Only advance the snapshot when time actually moved. Otherwise a
            // zero/negative-elapsed call would consume the pending deltas as
            // its new baseline and the next real measurement would report 0.
            if (elapsed > 0)
            {
                acceptRate = (accepted - _prevAccepted) / elapsed;
                sentRate = (sent - _prevSent) / elapsed;
                recvRate = (received - _prevReceived) / elapsed;

                _prevAccepted = accepted;
                _prevSent = sent;
                _prevReceived = received;
                _metricsTime = now;
            }

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
        Task drainTask;
        var ownsStop = false;

        lock (_stateLock)
        {
            // Nothing to do — but Stopping is deliberately NOT in this set. A caller that
            // finds the node already Stopping is a repeat or concurrent stop: it must not
            // own the stop, yet it still has to await the drain below instead of returning
            // with the node parked in Stopping and a caller unconvinced.
            if (_state is NodeState.Stopped or NodeState.Disposed or NodeState.Created)
            {
                return;
            }

            if (_state is not NodeState.Stopping)
            {
                // Exactly one caller performs the transition and, under the same lock,
                // becomes the owner of the single drain source. Ownership is what makes it
                // single: every later caller sees Stopping and creates nothing.
                _state = NodeState.Stopping;
                ownsStop = true;
                _drainSource = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                if (_connections.IsEmpty)
                {
                    // No connection can be tracked from here on (TryTrackConnection rejects
                    // Stopping), so OnConnectionClosed will never run: nothing to drain.
                    _drainSource.TrySetResult();
                }
            }

            // Null only when the state was forced to Stopping from outside (tests) — then
            // there is no stop in flight to wait for.
            drainTask = _drainSource?.Task ?? Task.CompletedTask;
        }

        // The caller's own token, kept before the internal deadline is linked in: the two
        // cancellations below are indistinguishable at the catch site, and only one of them is
        // the caller's business.
        var callerToken = cancellationToken;
        using var stopCts =
            Options.DrainTimeout > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
        if (stopCts is not null)
        {
            stopCts.CancelAfter(Options.DrainTimeout);
            cancellationToken = stopCts.Token;
        }

        var stopCompleted = false;
        var drainTimedOut = false;
        Exception? stopFault = null;

        try
        {
            // Safe for a repeat caller to skip this: it runs before the first await that can
            // surface a caller/token cancellation (the accept and idle waits swallow it), so
            // by the time the owner can abandon the stop, every connection has already been
            // asked to close and the drain below is all that is left to wait for.
            if (ownsStop)
            {
                // Each step of the close drive is isolated from the next. A fault escaping
                // from this prefix used to skip the connection close drive entirely: no
                // connection was asked to close, OnConnectionClosed never ran, the single
                // drain source was never signalled and the node stayed in Stopping for ever.
                // The first fault is reported and rethrown once the stop has finished, so it
                // is still loud — it just can no longer wedge the node.
                // Note the shape: every step is awaited even when an earlier one already
                // faulted (`??=` would short-circuit and skip the wait entirely, leaving a
                // background loop running while the connections are closed under it).
                stopFault ??= await CancelNodeSignalAsync().ConfigureAwait(false);

                try
                {
                    _listener.Close();
                }
                catch (Exception ex)
                {
                    ReportFault(NodeFaultCode.StopFailed, OperationStop, ex);
                    stopFault ??= ex;
                }

                var acceptLoopFault = await ObserveBackgroundLoopAsync(
                        _acceptTask,
                        cancellationToken,
                        OperationAccept
                    )
                    .ConfigureAwait(false);
                stopFault ??= acceptLoopFault;

                var idleLoopFault = await ObserveBackgroundLoopAsync(
                        _idleMonitorTask,
                        cancellationToken,
                        OperationStopIdle
                    )
                    .ConfigureAwait(false);
                stopFault ??= idleLoopFault;

                foreach (var connection in _connections.Values)
                {
                    connection.Close(TcpCloseReason.NodeStopping);
                }
            }

            // One task, every caller: completed by OnConnectionClosed once the last
            // connection is gone, so a second caller observes the outcome of the same drain
            // instead of waiting on a source that a later call replaced.
            await drainTask.WaitAsync(cancellationToken).ConfigureAwait(false);

            stopCompleted = true;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // A genuine caller cancellation. The deadline is the caller's, so the stop it
            // interrupted is not one this method may report as done: the node stays Stopping and a
            // later stop finishes it, which is what
            // StopAsync_cancellation_leaves_state_as_stopping_until_drain_finishes pins.
            throw;
        }
        catch (OperationCanceledException)
        {
            // DrainTimeout expired with a connection still closing. That deadline is the node's
            // own, not the caller's: surfacing it as an OperationCanceledException would make
            // `await using var node` throw on the way out of a host shutdown in which nothing was
            // cancelled. So the stop completes here instead (the same two-catch shape as
            // UdpNode.StopAsync).
            //
            // Nothing is hidden by completing: a connection that missed the deadline is still
            // running its own close, and every way that close can fail is reported at its site
            // (tcp.close.wait, tcp.close.handler, tcp.close.core). The timeout is a deadline on the
            // WAIT, not a verdict on the close.
            drainTimedOut = true;
        }
        finally
        {
            lock (_stateLock)
            {
                // A drained stop and a stop that ran out of deadline both end as Stopped: from the
                // caller's side the stop is over either way, and leaving the node in Stopping would
                // park every later stop on a drain nobody is left to finish in time.
                if (_state != NodeState.Disposed && (stopCompleted || drainTimedOut))
                {
                    _state = NodeState.Stopped;
                }
            }
        }

        if (stopFault is not null)
        {
            // The stop did what it could — every connection is closed and the drain was
            // observed — but the caller still learns that a step of it failed.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFault).Throw();
        }
    }

    /// <summary>
    /// Cancels the node-wide cancellation signal. A disposed source (a concurrent
    /// <see cref="DisposeAsync"/>) is a fault to report, not a reason to abandon the stop: the
    /// connections still have to be closed and the drain still has to be observed.
    /// </summary>
    private async Task<Exception?> CancelNodeSignalAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.StopFailed, OperationStopSignal, ex);
            return ex;
        }
    }

    /// <summary>
    /// Waits for a background loop to observe the stop. Cancellation is its expected exit and is
    /// not a fault; a loop that died with a real exception is captured (and reported) so the
    /// close drive that follows it still runs.
    /// </summary>
    private async Task<Exception?> ObserveBackgroundLoopAsync(
        Task? loop,
        CancellationToken cancellationToken,
        string operation
    )
    {
        if (loop is null)
        {
            return null;
        }

        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown — the loop exits via cancellation
            return null;
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.StopFailed, operation, ex);
            return ex;
        }
    }

    /// <summary>
    /// How many accept-fault backoffs the accept loop has COMPLETED (one per accept fault that
    /// reached the pause, counted only after the pause elapsed). Diagnostics for the one thing a
    /// test cannot otherwise observe without consulting a wall clock: that the loop suspends between
    /// two failed accepts instead of re-entering the accept wait immediately. At every instant it is
    /// <c>faults - 1</c> or <c>faults</c> — the next fault cannot be reported before its pause ran.
    /// </summary>
    internal long AcceptFaultBackoffs => Interlocked.Read(ref _acceptFaultBackoffs);

    /// <summary>
    /// The smallest pause the accept loop takes after a failed accept — see
    /// <see cref="EffectiveAcceptFaultBackoff"/>.
    /// </summary>
    internal static readonly TimeSpan AcceptFaultBackoffFloor = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The pause the accept loop takes after a failed accept, for a given
    /// <see cref="TcpNodeOptions.AcceptFaultBackoff"/>: the configured value clamped into
    /// [<see cref="AcceptFaultBackoffFloor"/>, <see cref="TcpConnectionLifecycle.MaxTimerDuration"/>].
    /// </summary>
    /// <remarks>
    /// A NON-POSITIVE value is the case that matters, and the reason the loop no longer branches on
    /// one. It used to `continue` with no suspension at all, so a persistently failing accept —
    /// WSAENOBUFS, EMFILE, an exhausted handle table — pinned a core and flooded the logger and every
    /// <see cref="OnFault"/> subscriber for as long as the condition lasted. The option keeps its
    /// documented meaning ("how long to pause after a failed accept"); a value that would switch the
    /// pause off is raised to the floor instead of switching it off.
    /// <para>
    /// The clamp lives HERE — where the loop reads the option — and not in the constructor, because
    /// the option is hot-reloadable and <see cref="ApplyConfigReload"/> takes whatever
    /// <see cref="TimeSpan.TryParse(string, out TimeSpan)"/> accepts: <c>00:00:00</c> and negative
    /// values included. The upper end is the timer limit for the same kind of reason: a pause
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> rejects would throw out of the accept
    /// loop's catch and end the loop for good, instead of pacing it.
    /// </para>
    /// </remarks>
    internal static TimeSpan EffectiveAcceptFaultBackoff(TimeSpan configured)
    {
        if (configured <= TimeSpan.Zero)
        {
            return AcceptFaultBackoffFloor;
        }

        return configured < TcpConnectionLifecycle.MaxTimerDuration
            ? configured
            : TcpConnectionLifecycle.MaxTimerDuration;
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
                catch (SocketException) when (_cts.IsCancellationRequested)
                {
                    // Graceful shutdown — accept was cancelled intentionally.
                    break;
                }
                catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ReportFault(NodeFaultCode.AcceptFailed, OperationAccept, ex);

                    // Never re-enter the accept wait without suspending: see
                    // EffectiveAcceptFaultBackoff. A non-positive backoff used to `continue` from
                    // here with no yield at all, so a persistently failing accept — WSAENOBUFS,
                    // EMFILE, an exhausted handle table — pinned a core and flooded the logger and
                    // every OnFault subscriber for as long as the condition lasted.
                    try
                    {
                        await Task.Delay(
                                EffectiveAcceptFaultBackoff(Options.AcceptFaultBackoff),
                                _cts.Token
                            )
                            .ConfigureAwait(false);
                        // Counted only once the pause has actually elapsed, so the counter can
                        // never claim a suspension that did not happen.
                        Interlocked.Increment(ref _acceptFaultBackoffs);
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
            // In-flight TLS handshakes count against MaxConnections too — a slow
            // handshake flood must not swamp the thread pool while bypassing the
            // connection cap (handshakes are not yet tracked in _connections).
            if (!TryBeginTlsHandshake())
            {
                Interlocked.Increment(ref _totalRejected);
                RejectAcceptedSocket(socket, NodeFaultCode.SessionRejected, OperationRejectLimit);
                return Task.CompletedTask;
            }

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
                finally
                {
                    EndTlsHandshake();
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
        var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
        try
        {
            await sslStream
                .AuthenticateAsServerAsync(Options.SslOptions!, _cts.Token)
                .ConfigureAwait(false);
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
        { /* expected during shutdown — idle monitor exits via cancellation */
        }
    }

    internal bool TryTrackConnection(TcpConnection connection)
    {
        lock (_stateLock)
        {
            if (_state is NodeState.Stopping or NodeState.Stopped or NodeState.Disposed)
            {
                return false;
            }

            if (_connections.Count >= Options.MaxConnections)
            {
                return false;
            }

            return _connections.TryAdd(connection.Id, connection);
        }
    }

    /// <summary>
    /// Reserves an admission slot for a TLS handshake that is not yet tracked
    /// in <see cref="_connections"/>. In-flight handshakes consume the same
    /// MaxConnections budget so slow-handshake floods cannot bypass the cap.
    /// </summary>
    internal bool TryBeginTlsHandshake()
    {
        lock (_stateLock)
        {
            if (_state is NodeState.Stopping or NodeState.Stopped or NodeState.Disposed)
            {
                return false;
            }

            if (_connections.Count + _tlsHandshakesInFlight >= Options.MaxConnections)
            {
                return false;
            }

            _tlsHandshakesInFlight++;
            return true;
        }
    }

    internal void EndTlsHandshake()
    {
        lock (_stateLock)
        {
            if (_tlsHandshakesInFlight > 0)
            {
                _tlsHandshakesInFlight--;
            }
        }
    }

    internal void OnConnectionClosed(TcpConnection connection)
    {
        Interlocked.Increment(ref _totalClosed);
        lock (_stateLock)
        {
            if (_connections.TryRemove(connection.Id, out _) && _connections.IsEmpty)
            {
                // The single drain source, signalled exactly once. TrySetResult keeps that
                // guarantee even when several closes race on the last removal.
                _drainSource?.TrySetResult();
            }
        }
    }

    public event Action<NodeFault>? OnFault;

    internal void ReportFault(NodeFaultCode code, string operation, Exception? exception = null)
    {
        var fault = new NodeFault(code, operation, exception);
        NodeHelper.ReportFault(Options.Logger, code, operation, exception);
        NodeHelper.RaiseFault(Options.Logger, OnFault, fault);
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
        while (!_configCts.IsCancellationRequested)
        {
            try
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
            catch (OperationCanceledException) when (_configCts.IsCancellationRequested)
            {
                // expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                // A transient reload failure must not kill the loop — log and
                // keep waiting for the next published change.
                Options.Logger?.Log(
                    LogLevel.Warning,
                    new EventId(0),
                    "Config reload failed (best-effort, continuing)",
                    ex
                );
            }
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
}
