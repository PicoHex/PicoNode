namespace PicoNode;

public sealed class UdpNode : INode, IAsyncDisposable
{
    private const string OperationStart = "udp.start";
    private const string OperationStop = "udp.stop.socket";
    private const string OperationSend = "udp.send";
    private const string OperationReceive = "udp.receive";
    private const string OperationQueueDrop = "udp.queue.drop";
    private const string OperationDatagramHandler = "udp.datagram.handler";
    private const long UdpConnectionIdTag = 2L << 56;
    private const string OperationMulticast = "udp.multicast";
    private const int MaxUdpDatagramSize = 65527;

    private readonly Socket _socket;
    private readonly CancellationTokenSource _receiveCts = new();

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
                    () => ProcessQueueAsync(queue, _receiveCts.Token),
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
        if (_state is NodeState.Stopped or NodeState.Disposed or NodeState.Created)
        {
            return;
        }

        var stopCompleted = false;

        lock (_stateLock)
        {
            if (_state is NodeState.Stopped or NodeState.Disposed or NodeState.Created)
            {
                return;
            }

            _state = NodeState.Stopping;
        }

        await _receiveCts.CancelAsync().ConfigureAwait(false);

        try
        {
            _socket.Close();
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.StopFailed, OperationStop, ex);
        }

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
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                if (_state != NodeState.Disposed && stopCompleted)
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
                    await Task.Delay(Options.ReceiveFaultBackoff, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
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
                            // Drain remaining datagrams from channel to return
                            // ArrayPool buffers before shutdown.
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
        { /* expected during shutdown �?datagram queue processing cancelled */
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
        OnFault?.Invoke(fault);
    }

    /// <summary>
    /// Background loop for configuration hot-reload.
    /// Survives <see cref="StopAsync"/> and is only stopped by
    /// <see cref="DisposeAsync"/> cancelling <see cref="_configCts"/>.
    /// </summary>
    private async Task ConfigReloadLoopAsync(ICfgRoot config, UdpNodeOptions options)
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
