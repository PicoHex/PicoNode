namespace PicoNode.Tests;

public sealed class TcpNodeBranchTests
{
    [Test]
    public async Task Constructor_rejects_invalid_numeric_options()
    {
        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            MaxConnections = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            ReceiveSocketBufferSize = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            SendSocketBufferSize = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            Backlog = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_rejects_negative_timeout_options()
    {
        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            IdleTimeout = TimeSpan.FromMilliseconds(-1),
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            AcceptFaultBackoff = TimeSpan.FromMilliseconds(-1),
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            DrainTimeout = TimeSpan.FromMilliseconds(-1),
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            IdleScanInterval = TimeSpan.FromMilliseconds(-1),
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_rejects_non_positive_idle_scan_interval()
    {
        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            IdleScanInterval = TimeSpan.Zero,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_rejects_non_positive_receive_pipe_pause_threshold()
    {
        await Assert
            .That(
                () =>
                    new TcpNode(
                        new TcpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            ConnectionHandler = new NoOpTcpHandler(),
                            ReceivePipePauseThresholdBytes = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_rejects_null_options()
    {
        await Assert.That(() => new TcpNode(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task RejectAcceptedSocket_reports_fault_and_disposes_socket()
    {
        var faults = new ConcurrentQueue<NodeFault>();
        var node = CreateNode(faults.Enqueue);
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        InvokeRejectAcceptedSocket(node, socket, NodeFaultCode.SessionRejected, "tcp.reject.limit");

        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults.TryPeek(out var fault)).IsTrue();
        await Assert.That(fault!.Code).IsEqualTo(NodeFaultCode.SessionRejected);
        await Assert.That(fault.Operation).IsEqualTo("tcp.reject.limit");
        await Assert
            .That(() => socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)))
            .Throws<ObjectDisposedException>();

        await node.DisposeAsync();
    }

    [Test]
    public async Task RejectAcceptedSocket_swallows_shutdown_failures_and_still_disposes_socket()
    {
        var faults = new ConcurrentQueue<NodeFault>();
        await using var node = CreateNode(faults.Enqueue);
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        socket.Dispose();

        InvokeRejectAcceptedSocket(node, socket, NodeFaultCode.SessionRejected, "tcp.reject.limit");

        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults.TryPeek(out var fault)).IsTrue();
        await Assert.That(fault!.Code).IsEqualTo(NodeFaultCode.SessionRejected);
        await Assert.That(fault.Operation).IsEqualTo("tcp.reject.limit");
    }

    [Test]
    public async Task ReportFault_returns_when_handler_is_null()
    {
        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = new NoOpTcpHandler(),
            }
        );

        InvokeReportFault(
            node,
            NodeFaultCode.ReceiveFailed,
            "tcp.receive",
            new InvalidOperationException("x")
        );

        await node.DisposeAsync();
    }

    [Test]
    public async Task ReportFault_swallows_fault_handler_exceptions()
    {
        var calls = 0;
        var node = CreateNode(_ =>
        {
            calls++;
            throw new InvalidOperationException("fault handler failed");
        });

        InvokeReportFault(
            node,
            NodeFaultCode.SendFailed,
            "tcp.send",
            new SocketException((int)SocketError.NetworkDown)
        );

        await Assert.That(calls).IsEqualTo(1);
        await node.DisposeAsync();
    }

    [Test]
    public async Task StartAsync_cannot_be_called_twice()
    {
        await using var node = CreateNode(_ => { });

        await node.StartAsync();

        await Assert.That(() => node.StartAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DisposeAsync_is_idempotent_and_sets_disposed_state()
    {
        var node = CreateNode(_ => { });

        await node.StartAsync();
        await node.DisposeAsync();
        await node.DisposeAsync();

        await Assert.That(node.State).IsEqualTo(NodeState.Disposed);
        await Assert.That(() => node.StartAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task LocalEndPoint_returns_configured_endpoint_before_start()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 43210);
        await using var node = CreateNode(new NoOpTcpHandler(), endpoint: endpoint);

        await Assert.That(node.LocalEndPoint).IsEqualTo(endpoint);
    }

    [Test]
    public async Task Constructor_applies_ipv6_dual_mode_when_requested()
    {
        var endpoint = new IPEndPoint(IPAddress.IPv6Loopback, 0);
        await using var node = CreateNode(
            new NoOpTcpHandler(),
            endpoint: endpoint,
            enableDualMode: true
        );

        await Assert.That(GetListener(node).DualMode).IsTrue();
    }

    [Test]
    public async Task StartAsync_reports_start_failed_when_bind_throws()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        using var blocker = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        )
        {
            ExclusiveAddressUse = true,
        };
        blocker.Bind(endpoint);
        blocker.Listen(1);

        var faults = new ConcurrentQueue<NodeFault>();
        await using var node = CreateNode(
            new NoOpTcpHandler(),
            faults.Enqueue,
            (IPEndPoint)blocker.LocalEndPoint!
        );

        var exception = await Assert.That(() => node.StartAsync()).Throws<SocketException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(faults.Count).IsEqualTo(1);
        await Assert.That(faults.TryPeek(out var fault)).IsTrue();
        await Assert.That(fault!.Code).IsEqualTo(NodeFaultCode.StartFailed);
        await Assert.That(fault.Operation).IsEqualTo("tcp.start");
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    [Test]
    public async Task StopAsync_closes_running_connections_with_node_stopping()
    {
        var handler = new RecordingTcpHandler();
        await using var node = CreateNode(handler);
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);

        var connected = await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await node.StopAsync();

        var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.That(connected.RemoteEndPoint).IsEqualTo((IPEndPoint)client.LocalEndPoint!);
        await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.NodeStopping);
        await Assert.That(closed.Error).IsNull();
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    [Test]
    public async Task StopAsync_cancellation_leaves_state_as_stopping_until_drain_finishes()
    {
        var handler = new RecordingTcpHandler(blockClose: true);
        await using var node = CreateNode(handler);
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopTask = node.StopAsync(stopCts.Token);
        await handler.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.That(async () => await stopTask).Throws<OperationCanceledException>();
        await Assert.That(node.State).IsEqualTo(NodeState.Stopping);

        handler.AllowClose();
        await node.StopAsync();
        await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    [Test]
    public async Task TryTrackConnection_returns_false_when_node_is_stopping()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode(_ => { });
            typeof(TcpNode)
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(node, NodeState.Stopping);

            var connection = new TcpConnection(node, pair.Server);
            var result = InvokeTryTrackConnection(node, connection);

            await Assert.That(result).IsFalse();
            await connection.DisposeAsync();
            await node.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task OnConnectionClosed_without_drain_target_removes_connection()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            await using var node = CreateNode(_ => { });
            var connection = new TcpConnection(node, pair.Server);

            var tracked = GetConnections(node).TryAdd(connection.Id, connection);
            await Assert.That(tracked).IsTrue();

            node.OnConnectionClosed(connection);

            await Assert.That(GetConnections(node).Count).IsEqualTo(0);
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task MonitorIdleConnectionsAsync_closes_idle_connections_with_idle_timeout_reason()
    {
        var handler = new RecordingTcpHandler();
        await using var node = CreateNode(
            handler,
            endpoint: new IPEndPoint(IPAddress.Loopback, 0),
            idleTimeout: TimeSpan.FromMilliseconds(150)
        );
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);

        var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.IdleTimeout);
        await Assert.That(closed.Error).IsNull();
    }

    [Test]
    public async Task MonitorIdleConnectionsAsync_does_not_delay_short_idle_timeout_beyond_scan_interval()
    {
        var handler = new RecordingTcpHandler();
        await using var node = CreateNode(
            handler,
            endpoint: new IPEndPoint(IPAddress.Loopback, 0),
            idleTimeout: TimeSpan.FromMilliseconds(150),
            idleScanInterval: TimeSpan.FromSeconds(5)
        );
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);

        var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.IdleTimeout);
        await Assert.That(closed.Error).IsNull();
    }

    [Test]
    public async Task MonitorIdleConnectionsAsync_remains_disabled_when_idle_timeout_is_zero()
    {
        var handler = new RecordingTcpHandler();
        await using var node = CreateNode(
            handler,
            endpoint: new IPEndPoint(IPAddress.Loopback, 0),
            idleTimeout: TimeSpan.Zero,
            idleScanInterval: TimeSpan.FromMilliseconds(10)
        );
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Task.Delay(250);

        await Assert.That(handler.Closed.Task.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ProcessAcceptedSocketAsync_reports_tracking_rejection_when_node_is_stopping()
    {
        var faults = new ConcurrentQueue<NodeFault>();
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode(faults.Enqueue);
            typeof(TcpNode)
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(node, NodeState.Stopping);

            await InvokeProcessAcceptedSocketAsync(node, pair.Server);

            await Assert.That(faults.Count).IsEqualTo(1);
            await Assert.That(faults.TryPeek(out var fault)).IsTrue();
            await Assert.That(fault!.Code).IsEqualTo(NodeFaultCode.SessionRejected);
            await Assert.That(fault.Operation).IsEqualTo("tcp.reject.tracking");
            await Assert
                .That(() => pair.Server.Send(new byte[] { 1 }, SocketFlags.None))
                .Throws<ObjectDisposedException>();

            await node.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
        }
    }

    private static TcpNode CreateNode(Action<NodeFault> faultHandler) =>
        CreateNode(new NoOpTcpHandler(), faultHandler);

    private static TcpNode CreateNode(
        ITcpConnectionHandler handler,
        Action<NodeFault>? faultHandler = null,
        IPEndPoint? endpoint = null,
        bool enableDualMode = false,
        TimeSpan? idleTimeout = null,
        TimeSpan? idleScanInterval = null
    ) =>
        new(
            new TcpNodeOptions
            {
                Endpoint = endpoint ?? new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = handler,
                FaultHandler = faultHandler,
                EnableDualMode = enableDualMode,
                IdleTimeout = idleTimeout ?? TimeSpan.Zero,
                IdleScanInterval = idleScanInterval ?? TimeSpan.FromSeconds(1),
            }
        );

    private static void InvokeRejectAcceptedSocket(
        TcpNode node,
        Socket socket,
        NodeFaultCode code,
        string operation
    )
    {
        var method = typeof(TcpNode).GetMethod(
            "RejectAcceptedSocket",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        method.Invoke(node, [socket, code, operation]);
    }

    private static void InvokeReportFault(
        TcpNode node,
        NodeFaultCode code,
        string operation,
        Exception? exception
    )
    {
        var method = typeof(TcpNode).GetMethod(
            "ReportFault",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        method.Invoke(node, [code, operation, exception]);
    }

    private static bool InvokeTryTrackConnection(TcpNode node, TcpConnection connection)
    {
        var method = typeof(TcpNode).GetMethod(
            "TryTrackConnection",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        return (bool)method.Invoke(node, [connection])!;
    }

    private static async Task InvokeProcessAcceptedSocketAsync(TcpNode node, Socket socket)
    {
        var method = typeof(TcpNode).GetMethod(
            "ProcessAcceptedSocketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        await (Task)method.Invoke(node, [socket])!;
    }

    private static Socket GetListener(TcpNode node)
    {
        var field = typeof(TcpNode).GetField(
            "_listener",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        return (Socket)field.GetValue(node)!;
    }

    private static ConcurrentDictionary<long, TcpConnection> GetConnections(TcpNode node)
    {
        var field = typeof(TcpNode).GetField(
            "_connections",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        return (ConcurrentDictionary<long, TcpConnection>)field.GetValue(node)!;
    }

    private static async Task<(Socket Client, Socket Server)> CreateConnectedSocketsAsync()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        var server = await listener.AcceptAsync();
        await connectTask;
        listener.Dispose();
        return (client, server);
    }

    private sealed class RecordingTcpHandler : ITcpConnectionHandler
    {
        private readonly bool _blockClose;
        private readonly TaskCompletionSource _allowClose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ConnectedTcpConnection> Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ClosedTcpConnection> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CloseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingTcpHandler(bool blockClose = false)
        {
            _blockClose = blockClose;
        }

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            Connected.TrySetResult(
                new ConnectedTcpConnection(connection.ConnectionId, connection.RemoteEndPoint)
            );
            return Task.CompletedTask;
        }

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            CloseStarted.TrySetResult();

            if (_blockClose)
            {
                return WaitForCloseAsync(reason, error, cancellationToken);
            }

            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
            return Task.CompletedTask;
        }

        public void AllowClose() => _allowClose.TrySetResult();

        private async Task WaitForCloseAsync(
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            await _allowClose.Task.WaitAsync(cancellationToken);
            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
        }
    }

    private sealed class NoOpTcpHandler : ITcpConnectionHandler
    {
        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed record ConnectedTcpConnection(long ConnectionId, IPEndPoint RemoteEndPoint);

    private sealed record ClosedTcpConnection(TcpCloseReason Reason, Exception? Error);
}
