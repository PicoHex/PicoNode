namespace PicoNode;

public sealed class UdpNode : INode
{
    private const string OperationStart = "udp.start";
    private const string OperationStop = "udp.stop.socket";
    private const string OperationSend = "udp.send";
    private const string OperationReceive = "udp.receive";
    private const string OperationQueueDrop = "udp.queue.drop";
    private const string OperationDatagramHandler = "udp.datagram.handler";
    private const string OperationStopHandlers = "udp.stop.handlers";
    private const long UdpConnectionIdTag = 2L << 56;
    private const string OperationMulticast = "udp.multicast";
    private const int MaxUdpDatagramSize = 65527;

    private readonly Socket _socket;

    /// <summary>
    /// Lifetime of the datagram RECEIVE loop, and nothing else: <see cref="StopAsync"/> cancels it
    /// first, which is what fixes "no datagram is queued after this point".
    /// </summary>
    private readonly CancellationTokenSource _receiveCts = new();

    /// <summary>
    /// Lifetime of the datagram HANDLERS, deliberately separate from <see cref="_receiveCts"/>: it is
    /// the token <see cref="UdpNodeOptions.DatagramHandler"/> receives, so it has to outlive the stop's
    /// drain. The dispatch workers keep delivering what is already queued while
    /// <see cref="StopAsync"/> waits for them, and only a drain that does NOT finish — or
    /// <see cref="DisposeAsync"/> — cancels it.
    /// </summary>
    /// <remarks>
    /// Sharing the receive lifetime is what used to abort every in-flight AND queued datagram the
    /// instant a stop began: the workers' handler token was <see cref="_receiveCts"/>'s token, so the
    /// first thing <see cref="StopAsync"/> did was cancel the token each handler was waiting on, and the
    /// worker's <see cref="OperationCanceledException"/> branch then discarded the rest of the queue.
    /// <see cref="INode.StopAsync"/> is documented as draining in-flight work up to the drain timeout;
    /// two lifetimes are what make that true for datagrams.
    /// </remarks>
    private readonly CancellationTokenSource _handlerCts = new();

    /// <summary>
    /// Cancellation token source for the config reload loop.
    /// NOT cancelled during <see cref="StopAsync"/> — the config loop survives
    /// until <see cref="DisposeAsync"/> cancels it, allowing the last reload to
    /// complete during the stop→dispose window.
    /// </summary>
    private readonly CancellationTokenSource _configCts = new();
    private readonly Channel<UdpDatagramLease>[] _queues;
    private readonly Task[] _workers;
    private readonly Lock _stateLock = new();
    private Task? _receiveTask;

    /// <summary>
    /// Background task for configuration hot-reload. Started in <see cref="StartAsync"/>
    /// and survives <see cref="StopAsync"/>. Only cancelled in <see cref="DisposeAsync"/>.
    /// </summary>
    private Task? _configReloadTask;
    private volatile NodeState _state;
    private int _disposed;
    private static long _nextConnectionId;
    private long _totalDatagramsReceived;
    private long _totalDatagramsSent;
    private long _totalBytesReceived;
    private long _totalBytesSent;
    private long _totalDropped;
    private long _receiveFaultBackoffs;

    /// <summary>Returns a snapshot of cumulative UDP metrics.</summary>
    public UdpNodeMetrics GetMetrics() =>
        new(
            TotalDatagramsSent: Interlocked.Read(ref _totalDatagramsSent),
            TotalDatagramsReceived: Interlocked.Read(ref _totalDatagramsReceived),
            TotalDatagramsDropped: Interlocked.Read(ref _totalDropped),
            TotalBytesSent: Interlocked.Read(ref _totalBytesSent),
            TotalBytesReceived: Interlocked.Read(ref _totalBytesReceived)
        );

    public UdpNode(UdpNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.DatagramHandler);
        Options = options;
        if (options.DispatchWorkerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.DispatchWorkerCount));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DatagramQueueCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ReceiveSocketBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.SendSocketBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ReceiveDatagramBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.ReceiveDatagramBufferSize,
            MaxUdpDatagramSize
        );
        if (options.ReceiveFaultBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ReceiveFaultBackoff));
        }

        _socket = new Socket(options.Endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = options.ReceiveSocketBufferSize,
            SendBufferSize = options.SendSocketBufferSize,
            EnableBroadcast = options.EnableBroadcast,
        };

        _queues = new Channel<UdpDatagramLease>[options.DispatchWorkerCount];
        _workers = new Task[options.DispatchWorkerCount];
        for (var i = 0; i < options.DispatchWorkerCount; i++)
        {
            _queues[i] = Channel.CreateBounded<UdpDatagramLease>(
                new BoundedChannelOptions(options.DatagramQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                }
            );
        }

        _state = NodeState.Created;
    }

    internal UdpNodeOptions Options { get; }

    public EndPoint LocalEndPoint => _socket.LocalEndPoint ?? Options.Endpoint;

    public NodeState State => _state;

    public void JoinMulticastGroup(IPAddress groupAddress)
    {
        ArgumentNullException.ThrowIfNull(groupAddress);

        try
        {
            _socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(groupAddress)
            );
            _socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastTimeToLive,
                Options.MulticastTtl
            );
            _socket.MulticastLoopback = Options.MulticastLoopback;
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.StartFailed, OperationMulticast, ex);
            throw;
        }
    }

    public void LeaveMulticastGroup(IPAddress groupAddress)
    {
        ArgumentNullException.ThrowIfNull(groupAddress);

        try
        {
            _socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.DropMembership,
                new MulticastOption(groupAddress)
            );
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.StopFailed, OperationMulticast, ex);
            throw;
        }
    }

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
            _socket.Bind(Options.Endpoint);

            if (Options.MulticastGroup is not null)
            {
                JoinMulticastGroup(Options.MulticastGroup);
            }

            for (var i = 0; i < _workers.Length; i++)
            {
                var queue = _queues[i];
                _workers[i] = Task.Run(
                    () => ProcessQueueAsync(queue, _handlerCts.Token),
                    CancellationToken.None
                );
            }

            _receiveTask = Task.Run(
                () => ReceiveLoopAsync(_receiveCts.Token),
                CancellationToken.None
            );
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
        if (
            _state
            is NodeState.Stopping
                or NodeState.Stopped
                or NodeState.Disposed
                or NodeState.Created
        )
        {
            return;
        }

        // The deadline timer gets the CLAMPED budget: CancelAfter rejects a delay above the timer
        // limit, and that call sits before the drain's own try, so a huge DrainTimeout used to skip the
        // drain entirely instead of shortening it.
        var callerToken = cancellationToken;
        var drainBudget = DrainBudget.Effective(Options.DrainTimeout);
        using var stopCts =
            drainBudget > TimeSpan.Zero
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
        if (stopCts is not null)
        {
            stopCts.CancelAfter(drainBudget);
            cancellationToken = stopCts.Token;
        }

        var stopCompleted = false;

        lock (_stateLock)
        {
            if (
                _state
                is NodeState.Stopping
                    or NodeState.Stopped
                    or NodeState.Disposed
                    or NodeState.Created
            )
            {
                return;
            }

            _state = NodeState.Stopping;
        }

        // 1. Stop the intake. The receive loop is the only writer to the dispatch queues, so its
        //    lifetime is what decides "no datagram is queued from here on" — a datagram still in the
        //    socket is abandoned with the loop, one already queued is not.
        await _receiveCts.CancelAsync().ConfigureAwait(false);

        try
        {
            _socket.Close();
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.StopFailed, OperationStop, ex);
        }

        // 2. Complete the queues, then drain them. Completion is what lets a worker finish on its own
        //    once the buffer is empty, and the handlers still run on _handlerCts: a datagram queued
        //    before the stop is DELIVERED during the stop, which is the whole point of keeping the two
        //    lifetimes apart.
        foreach (var queue in _queues)
        {
            queue.Writer.TryComplete();
        }

        try
        {
            if (_receiveTask is not null)
            {
                await _receiveTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
            stopCompleted = true;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // A genuine caller cancellation: the drain is abandoned, not completed. A later StopAsync on
            // this node is a no-op (the state is already Stopping/Stopped and the queues are complete),
            // so nothing is left to drain for it — the finally below releases the handler lifetime
            // instead of leaving handlers parked on it for ever.
            throw;
        }
        catch (OperationCanceledException)
        {
            // The drain budget expired with datagrams still queued or a handler still running: that,
            // and only that, is when the handlers' own lifetime is cancelled (in the finally below).
            // Complete gracefully without rethrowing, as before.
        }
        finally
        {
            // The one rule for the handler lifetime: it is cancelled when — and only when — the drain
            // did not complete (budget expired, the caller cancelled, a worker faulted). A stop that
            // drained keeps it quiet, because nothing is left to observe it.
            if (!stopCompleted)
            {
                await AbortHandlersAsync().ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                if (_state != NodeState.Disposed && stopCompleted)
                {
                    _state = NodeState.Stopped;
                }
                else if (_state == NodeState.Stopping)
                {
                    _state = NodeState.Stopped;
                }
            }
        }
    }

    internal async Task SendAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _socket.SendToAsync(
                datagram,
                SocketFlags.None,
                remoteEndPoint,
                cancellationToken
            );
            Interlocked.Increment(ref _totalDatagramsSent);
            Interlocked.Add(ref _totalBytesSent, datagram.Length);
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.SendFailed, OperationSend, ex);
            throw;
        }
    }

    /// <summary>
    /// How many receive-fault backoffs the datagram receive loop has COMPLETED (one per receive fault
    /// that reached the pause, counted only after the pause elapsed). Diagnostics for the one thing a
    /// test cannot otherwise observe without consulting a wall clock: that the loop suspends between two
    /// failed receives instead of re-entering the receive immediately. At every instant it is
    /// <c>faults - 1</c> or <c>faults</c> — the next fault cannot be reported before its pause ran.
    /// </summary>
    internal long ReceiveFaultBackoffs => Interlocked.Read(ref _receiveFaultBackoffs);

    /// <summary>
    /// The smallest pause the datagram receive loop takes after a failed receive — see
    /// <see cref="EffectiveReceiveFaultBackoff"/>.
    /// </summary>
    internal static readonly TimeSpan ReceiveFaultBackoffFloor = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The pause the datagram receive loop takes after a failed receive, for a given
    /// <see cref="UdpNodeOptions.ReceiveFaultBackoff"/>: a NON-POSITIVE value is REPLACED by
    /// <see cref="ReceiveFaultBackoffFloor"/> (1 ms), and a positive value is capped at
    /// <see cref="DrainBudget.MaxTimerDuration"/>. Between the two ends the configured value is used
    /// unchanged — a 0.5 ms backoff still means 0.5 ms, not the floor.
    /// </summary>
    /// <remarks>
    /// A NON-POSITIVE value is the case that matters, and the reason the loop no longer hands the
    /// option to <see cref="Task.Delay(TimeSpan, CancellationToken)"/> as it stands: zero produced an
    /// already-completed task, so the loop re-entered the receive with no suspension at all. The option
    /// keeps its documented meaning ("how long to pause after a failed receive"); a value that would
    /// switch the pause off is raised to the floor instead of switching it off.
    /// <para>
    /// The clamp lives HERE — where the loop reads the option — and not in the constructor, because the
    /// option is hot-reloadable and <see cref="ApplyConfigReload"/> takes whatever
    /// <see cref="TimeSpan.TryParse(string, out TimeSpan)"/> accepts: <c>00:00:00</c> and negative
    /// values included. The upper end is the timer limit for the same kind of reason: a delay
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> rejects would throw out of the receive
    /// loop's catch and end the loop for good, instead of pacing it.
    /// </para>
    /// </remarks>
    internal static TimeSpan EffectiveReceiveFaultBackoff(TimeSpan configured)
    {
        if (configured <= TimeSpan.Zero)
        {
            return ReceiveFaultBackoffFloor;
        }

        return configured < DrainBudget.MaxTimerDuration
            ? configured
            : DrainBudget.MaxTimerDuration;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        EndPoint remoteEndPoint = new IPEndPoint(
            Options.Endpoint.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any
                : IPAddress.Any,
            0
        );

        while (!cancellationToken.IsCancellationRequested)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Options.ReceiveDatagramBufferSize);

            try
            {
                var result = await _socket.ReceiveFromAsync(
                    new ArraySegment<byte>(buffer),
                    SocketFlags.None,
                    remoteEndPoint,
                    cancellationToken
                );

                var sender = (IPEndPoint)result.RemoteEndPoint;
                var lease = new UdpDatagramLease(buffer, result.ReceivedBytes, sender);
                var queue = _queues[GetQueueIndex(sender)];

                Interlocked.Increment(ref _totalDatagramsReceived);
                Interlocked.Add(ref _totalBytesReceived, result.ReceivedBytes);

                if (Options.QueueOverflowMode == UdpOverflowMode.Wait)
                {
                    await queue.Writer.WriteAsync(lease, cancellationToken).ConfigureAwait(false);
                }
                else if (!queue.Writer.TryWrite(lease))
                {
                    lease.Dispose();
                    Interlocked.Increment(ref _totalDropped);
                    ReportFault(NodeFaultCode.DatagramDropped, OperationQueueDrop);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                break;
            }
            catch (Exception ex)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ReportFault(NodeFaultCode.DatagramReceiveFailed, OperationReceive, ex);
                try
                {
                    // Never re-enter the receive without suspending: see
                    // EffectiveReceiveFaultBackoff. A non-positive backoff used to hand Task.Delay a
                    // zero delay — an already-completed task — so a permanently failing socket (closed
                    // underneath the loop, an exhausted handle table) pinned a core and raised one
                    // DatagramReceiveFailed fault per iteration for as long as the fault lasted.
                    await Task.Delay(
                            EffectiveReceiveFaultBackoff(Options.ReceiveFaultBackoff),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    // Counted only once the pause has actually elapsed, so the counter can never
                    // claim a suspension that did not happen.
                    Interlocked.Increment(ref _receiveFaultBackoffs);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Cancels the datagram handlers' own lifetime — the reason it is kept apart from the receive
    /// lifetime. Only the give-up paths call this: a stop whose drain did not complete, and
    /// <see cref="DisposeAsync"/>. A stop that fits in its budget leaves it alone, so the queue is
    /// delivered instead of discarded.
    /// </summary>
    private async Task AbortHandlersAsync()
    {
        try
        {
            await _handlerCts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cancellation callbacks run user code and one of them threw. The node has already given up
            // on the drain, so keep going — loudly.
            ReportFault(NodeFaultCode.StopFailed, OperationStopHandlers, ex);
        }
    }

    /// <summary>True when no worker can still be inside a handler.</summary>
    private bool HandlersHaveExited()
    {
        foreach (var worker in _workers)
        {
            if (worker is not null && !worker.IsCompleted)
            {
                return false;
            }
        }

        return true;
    }

    private async Task ProcessQueueAsync(
        Channel<UdpDatagramLease> queue,
        CancellationToken handlerCancellationToken
    )
    {
        try
        {
            var reader = queue.Reader;
            // Channel completion (TryComplete) controls loop exit so remaining
            // datagrams are drained even during shutdown.
            while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (reader.TryRead(out var lease))
                {
                    using (lease)
                    {
                        try
                        {
                            var context = new UdpDatagramContext(
                                this,
                                UdpConnectionIdTag
                                    | (
                                        Interlocked.Increment(ref _nextConnectionId)
                                        & 0x00FFFFFFFFFFFFFFL
                                    ),
                                lease.RemoteEndPoint
                            );
                            var handleTask = Options.DatagramHandler.OnDatagramAsync(
                                context,
                                lease.Datagram,
                                handlerCancellationToken
                            );
                            if (!handleTask.IsCompletedSuccessfully)
                            {
                                await handleTask.ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException)
                            when (handlerCancellationToken.IsCancellationRequested)
                        {
                            // The handler lifetime was cancelled, i.e. the drain gave up (see
                            // AbortHandlersAsync) — there is nothing left to deliver, only buffers to
                            // return to ArrayPool.
                            while (reader.TryRead(out var remainingLease))
                            {
                                remainingLease.Dispose();
                            }
                            return;
                        }
                        catch (Exception ex)
                        {
                            ReportFault(
                                NodeFaultCode.DatagramHandlerFailed,
                                OperationDatagramHandler,
                                ex
                            );
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (handlerCancellationToken.IsCancellationRequested)
        { /* expected during shutdown — datagram queue processing cancelled */
        }
    }

    private int GetQueueIndex(IPEndPoint remoteEndPoint)
    {
        var hash = HashCode.Combine(remoteEndPoint.Address, remoteEndPoint.Port);
        hash = hash == int.MinValue ? 0 : Math.Abs(hash);
        return hash % _queues.Length;
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
    /// </summary>
    private async Task ConfigReloadLoopAsync(ICfgRoot config, UdpNodeOptions options)
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

    private static void ApplyConfigReload(ICfg config, UdpNodeOptions options)
    {
        if (
            config.TryGetValue("ReceiveSocketBufferSize", out var v) && int.TryParse(v, out var val)
        )
            options.ReceiveSocketBufferSize = val;
        if (config.TryGetValue("SendSocketBufferSize", out v) && int.TryParse(v, out val))
            options.SendSocketBufferSize = val;
        if (config.TryGetValue("ReceiveDatagramBufferSize", out v) && int.TryParse(v, out val))
            options.ReceiveDatagramBufferSize = val;
        if (config.TryGetValue("EnableBroadcast", out v) && bool.TryParse(v, out var b))
            options.EnableBroadcast = b;
        if (
            config.TryGetValue("QueueOverflowMode", out v)
            && Enum.TryParse<UdpOverflowMode>(v, out var mode)
        )
            options.QueueOverflowMode = mode;
        if (config.TryGetValue("ReceiveFaultBackoff", out v) && TimeSpan.TryParse(v, out var ts))
            options.ReceiveFaultBackoff = ts;
        if (config.TryGetValue("MulticastTtl", out v) && int.TryParse(v, out val))
            options.MulticastTtl = val;
        if (config.TryGetValue("MulticastLoopback", out v) && bool.TryParse(v, out b))
            options.MulticastLoopback = b;
        // Endpoint, DatagramHandler, Logger, DispatchWorkerCount, DatagramQueueCapacity, MulticastGroup are code-only
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
            _receiveCts.Dispose();
            // A handler that outlived the drain (one that ignores its token) must not be left with a
            // live lifetime, so cancel it. Dispose it only once no worker can be inside a handler: a
            // disposed source makes Cancel/Register on its token throw, and that throw would land in
            // user handler code rather than here.
            await AbortHandlersAsync().ConfigureAwait(false);
            if (HandlersHaveExited())
            {
                _handlerCts.Dispose();
            }

            _socket.Dispose();
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
