namespace Pico.Node;

public sealed class UdpNode : INode, IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<UdpDatagramLease>[] _queues;
    private readonly Task[] _workers;
    private Task? _receiveTask;
    private volatile NodeState _state;
    private bool _disposed;

    public UdpNode(UdpNodeOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.WorkerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.WorkerCount));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.QueueCapacityPerWorker, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ReceiveBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.SendBufferSize, 0);

        _socket = new Socket(options.Endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = options.ReceiveBufferSize,
            SendBufferSize = options.SendBufferSize,
            EnableBroadcast = options.EnableBroadcast,
        };

        _queues = new Channel<UdpDatagramLease>[options.WorkerCount];
        _workers = new Task[options.WorkerCount];
        for (var i = 0; i < options.WorkerCount; i++)
        {
            _queues[i] = Channel.CreateBounded<UdpDatagramLease>(
                new BoundedChannelOptions(options.QueueCapacityPerWorker)
                {
                    FullMode = options.OverflowMode == UdpOverflowMode.Wait
                        ? BoundedChannelFullMode.Wait
                        : BoundedChannelFullMode.DropWrite,
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

        if (_state != NodeState.Created)
        {
            throw new InvalidOperationException("Node can only be started once.");
        }

        _state = NodeState.Starting;

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

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
            _state = NodeState.Running;
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _state = NodeState.Stopped;
            ReportFault(NodeFaultCode.StartFailed, "udp-start", ex);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_state is NodeState.Stopped or NodeState.Disposed or NodeState.Created)
        {
            return;
        }

        _state = NodeState.Stopping;
        _cts.Cancel();

        try
        {
            _socket.Close();
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.StopFailed, "udp-stop-close-socket", ex);
        }

        foreach (var queue in _queues)
        {
            queue.Writer.TryComplete();
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }

        _state = NodeState.Stopped;
    }

    internal async Task SendAsync(
        ArraySegment<byte> datagram,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _socket.SendToAsync(datagram, SocketFlags.None, remoteEndPoint, cancellationToken);
        }
        catch (Exception ex)
        {
            ReportFault(NodeFaultCode.SendFailed, "udp-send", ex);
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
            var buffer = ArrayPool<byte>.Shared.Rent(65527);

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

                if (Options.OverflowMode == UdpOverflowMode.Wait)
                {
                    await queue.Writer.WriteAsync(lease, cancellationToken);
                }
                else if (!queue.Writer.TryWrite(lease))
                {
                    lease.Dispose();
                    ReportFault(NodeFaultCode.UdpDatagramDropped, "udp-queue-full");
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
                ReportFault(NodeFaultCode.UdpReceiveFailed, "udp-receive", ex);
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

    private async Task ProcessQueueAsync(Channel<UdpDatagramLease> queue, CancellationToken cancellationToken)
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
                        var handleTask = Options.Handler.OnDatagramAsync(
                            context,
                            lease.Datagram,
                            cancellationToken
                        );
                        if (!handleTask.IsCompletedSuccessfully)
                        {
                            await handleTask;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        ReportFault(NodeFaultCode.UdpHandlerFailed, "udp-handler", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private int GetQueueIndex(IPEndPoint remoteEndPoint)
    {
        var hash = HashCode.Combine(remoteEndPoint.Address, remoteEndPoint.Port);
        hash = hash == int.MinValue ? 0 : Math.Abs(hash);
        return hash % _queues.Length;
    }

    internal void ReportFault(NodeFaultCode code, string operation, Exception? exception = null)
    {
        var faultHandler = Options.FaultHandler;
        if (faultHandler is null)
        {
            return;
        }

        try
        {
            faultHandler(new NodeFault(code, operation, exception));
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _cts.Dispose();
        _socket.Dispose();
        _state = NodeState.Disposed;
        GC.SuppressFinalize(this);
    }
}
