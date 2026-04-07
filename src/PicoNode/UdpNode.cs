namespace PicoNode;

public sealed class UdpNode : INode, IAsyncDisposable
{
    private const string OperationStart = "udp.start";
    private const string OperationStop = "udp.stop.socket";
    private const string OperationSend = "udp.send";
    private const string OperationReceive = "udp.receive";
    private const string OperationQueueDrop = "udp.queue.drop";
    private const string OperationDatagramHandler = "udp.datagram.handler";
    private const int MaxUdpDatagramSize = 65527;

    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly Channel<UdpDatagramLease>[] _queues;
    private readonly Task[] _workers;
    private readonly Lock _stateLock = new();
    private Task? _receiveTask;
    private volatile NodeState _state;
    private bool _disposed;

    public UdpNode(UdpNodeOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
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
            _workers[i] = Task.CompletedTask;
        }

        _state = NodeState.Created;
    }

    internal UdpNodeOptions Options { get; }

    public EndPoint LocalEndPoint => _socket.LocalEndPoint ?? Options.Endpoint;

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
            _socket.Bind(Options.Endpoint);
            for (var i = 0; i < _workers.Length; i++)
            {
                var queue = _queues[i];
                _workers[i] = Task.Run(
                    () => ProcessQueueAsync(queue, _cts.Token),
                    CancellationToken.None
                );
            }

            _receiveTask = Task.Run(
                () => ReceiveLoopAsync(_receiveCts.Token),
                CancellationToken.None
            );
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

        lock (_stateLock)
        {
            if (_state is NodeState.Stopped or NodeState.Disposed or NodeState.Created)
            {
                return;
            }

            _state = NodeState.Stopping;
        }

        _receiveCts.Cancel();

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
                await _receiveTask.WaitAsync(cancellationToken);
            }

            await Task.WhenAll(_workers).WaitAsync(cancellationToken);
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
                if (_state != NodeState.Disposed)
                {
                    _state = NodeState.Stopped;
                }
            }
        }
    }

    internal async Task SendAsync(
        ArraySegment<byte> datagram,
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

                if (Options.QueueOverflowMode == UdpOverflowMode.Wait)
                {
                    await queue.Writer.WriteAsync(lease, cancellationToken);
                }
                else if (!queue.Writer.TryWrite(lease))
                {
                    lease.Dispose();
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
                    await Task.Delay(10, cancellationToken);
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
        CancellationToken cancellationToken
    )
    {
        try
        {
            await foreach (var lease in queue.Reader.ReadAllAsync(cancellationToken))
            {
                using (lease)
                {
                    try
                    {
                        var context = new UdpDatagramContext(this, lease.RemoteEndPoint);
                        var handleTask = Options
                            .DatagramHandler
                            .OnDatagramAsync(context, lease.Datagram, cancellationToken);
                        if (!handleTask.IsCompletedSuccessfully)
                        {
                            await handleTask;
                        }
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        break;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private int GetQueueIndex(IPEndPoint remoteEndPoint)
    {
        var hash = HashCode.Combine(remoteEndPoint.Address, remoteEndPoint.Port);
        hash = hash == int.MinValue ? 0 : Math.Abs(hash);
        return hash % _queues.Length;
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
            _receiveCts.Dispose();
            _cts.Dispose();
            _socket.Dispose();
            _state = NodeState.Disposed;
            GC.SuppressFinalize(this);
        }

        if (stopException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopException).Throw();
        }
    }
}
