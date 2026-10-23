namespace PicoNode.Tests;

public sealed class TcpNodeBranchTests
{
    [Test]
    public async Task Constructor_rejects_invalid_numeric_options()
    {
        await Assert
            .That(() =>
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
            .That(() =>
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
            .That(() =>
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
            .That(() =>
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
            .That(() =>
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
            .That(() =>
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
            .That(() =>
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
            .That(() =>
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
            .That(() =>
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
            .That(() =>
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
        var logger = new SpyLogger();
        var node = CreateNode(logger);
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        InvokeRejectAcceptedSocket(node, socket, NodeFaultCode.SessionRejected, "tcp.reject.limit");

        await Assert.That(logger.Calls.Count).IsEqualTo(2);
        await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
        await Assert.That(call!.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.SessionRejected);
        await Assert
            .That(() => socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)))
            .Throws<ObjectDisposedException>();

        await node.DisposeAsync();
    }

    [Test]
    public async Task RejectAcceptedSocket_swallows_shutdown_failures_and_still_disposes_socket()
    {
        var logger = new SpyLogger();
        await using var node = CreateNode(logger);
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        socket.Dispose();

        InvokeRejectAcceptedSocket(node, socket, NodeFaultCode.SessionRejected, "tcp.reject.limit");

        await Assert.That(logger.Calls.Count).IsEqualTo(2);
        await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
        await Assert.That(call!.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.SessionRejected);
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
        var logger = new SpyLogger { ThrowOnLog = true };
        var node = CreateNode(logger);

        InvokeReportFault(
            node,
            NodeFaultCode.SendFailed,
            "tcp.send",
            new SocketException((int)SocketError.NetworkDown)
        );

        await Assert.That(logger.Calls.Count).IsEqualTo(1);
        await node.DisposeAsync();
    }

    [Test]
    public async Task ReportFault_swallows_OnFault_subscriber_exceptions()
    {
        await using var node = CreateNode(null);
        node.OnFault += _ => throw new InvalidOperationException("subscriber boom");

        await Assert
            .That(() => node.ReportFault(NodeFaultCode.AcceptFailed, "tcp.accept"))
            .ThrowsNothing()
            .Because("a fault subscriber must never destabilize the node's loops");
    }

    [Test]
    public async Task ReportFault_delivers_to_all_OnFault_subscribers_when_one_throws()
    {
        await using var node = CreateNode(null);
        var secondCalled = false;
        node.OnFault += _ => throw new InvalidOperationException("first boom");
        node.OnFault += _ => secondCalled = true;

        node.ReportFault(NodeFaultCode.AcceptFailed, "tcp.accept");

        await Assert
            .That(secondCalled)
            .IsTrue()
            .Because("one misbehaving subscriber must not starve the remaining subscribers");
    }

    [Test]
    public async Task StartAsync_cannot_be_called_twice()
    {
        await using var node = CreateNode(null);

        await node.StartAsync();

        await Assert.That(() => node.StartAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DisposeAsync_is_idempotent_and_sets_disposed_state()
    {
        var node = CreateNode(null);

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

        var logger = new SpyLogger();
        await using var node = CreateNode(
            new NoOpTcpHandler(),
            logger,
            (IPEndPoint)blocker.LocalEndPoint!
        );

        var exception = await Assert.That(() => node.StartAsync()).Throws<SocketException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(logger.Calls.Count).IsEqualTo(1);
        await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
        await Assert.That(call!.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.StartFailed);
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
    public async Task StopAsync_twice_with_zero_drain_does_not_hang()
    {
        var handler = new RecordingTcpHandler(blockClose: true);
        await using var node = CreateNode(handler, drainTimeout: TimeSpan.Zero);
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // DrainTimeout.Zero is legal and means "no deadline at all" (no stop CTS is
        // created), so a caller parked on a drain source nobody signals waits for ever
        // and the host cannot shut down. Fixed racer count (2), released through a TCS —
        // no delays: the first stop is provably inside the close path before the second
        // one is started.
        var first = node.StopAsync();
        await handler.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = node.StopAsync();

        handler.AllowClose();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    [Test]
    public async Task StopAsync_second_call_observes_Stopped()
    {
        var handler = new RecordingTcpHandler(blockClose: true);
        await using var node = CreateNode(handler, drainTimeout: TimeSpan.Zero);
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var first = node.StopAsync();
        await handler.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Second caller arrives while the node is Stopping: it must join the single
        // drain source and complete the stop instead of minting a second one.
        var second = node.StopAsync();

        handler.AllowClose();

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);

        // Once the stop completed, a repeat call returns without touching the state.
        await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    [Test]
    public async Task A_faulted_stop_still_closes_connections_and_drains()
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
        await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // A background loop that died with a non-cancellation fault is what StopAsync's
        // owner-only prefix awaits. A fault escaping there used to skip the connection close
        // drive entirely: no connection was asked to close, OnConnectionClosed never ran,
        // the single drain source was never signalled and the node stayed in Stopping for
        // ever (the host could not shut down). The close drive must run anyway, and the
        // fault must still reach the caller instead of being swallowed.
        var acceptFault = new InvalidOperationException("accept loop died");
        typeof(TcpNode)
            .GetField("_acceptTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(node, Task.FromException(acceptFault));

        var stopTask = node.StopAsync();

        var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.NodeStopping);

        var exception = await Assert
            .That(async () => await stopTask)
            .Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).IsEqualTo("accept loop died");
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    [Test]
    public async Task DisposeAsync_does_not_throw_when_drain_times_out()
    {
        var handler = new RecordingTcpHandler(blockClose: true);
        var node = CreateNode(handler, drainTimeout: TimeSpan.FromMilliseconds(50));
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        try
        {
            // The close handler is parked, so the drain cannot complete on its own: the node's
            // own DrainTimeout (50 ms) is the only thing that can end the wait. A caller that
            // cancelled nothing must not be handed an OperationCanceledException for that
            // internal deadline — `await using var node` would then throw on the way out of a
            // host shutdown that otherwise succeeded.
            await Assert
                .That(async () => await node.DisposeAsync())
                .ThrowsNothing()
                .Because("the internal drain deadline is not the caller's cancellation");

            // Proof the dispose returned at its own deadline instead of waiting the parked close
            // handler out: the handler is still parked when the caller is already released.
            await Assert
                .That(handler.Closed.Task.IsCompleted)
                .IsFalse()
                .Because("the caller was released by DrainTimeout, not by the close finishing");
            await Assert.That(node.State).IsEqualTo(NodeState.Disposed);
        }
        finally
        {
            handler.AllowClose();
        }

        var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert
            .That(closed.Reason)
            .IsEqualTo(TcpCloseReason.NodeStopping)
            .Because("swallowing the deadline must not abandon the close that was still running");
    }

    [Test]
    public async Task StopAsync_swallows_the_drain_timeout_and_reaches_Stopped()
    {
        var handler = new RecordingTcpHandler(blockClose: true);
        var node = CreateNode(handler, drainTimeout: TimeSpan.FromMilliseconds(50));
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        try
        {
            await Assert.That(async () => await node.StopAsync()).ThrowsNothing();

            // A stop whose own deadline expired is over from the caller's point of view: leaving
            // the node in Stopping would park every later stop on a drain nobody is left to
            // finish in time.
            await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
            await Assert
                .That(async () => await node.StopAsync())
                .ThrowsNothing()
                .Because("a repeat stop after a timed-out drain finds a Stopped node");
        }
        finally
        {
            handler.AllowClose();
        }

        await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await node.DisposeAsync();
    }

    [Test]
    public async Task The_handler_loop_bound_is_strictly_inside_the_drain_budget()
    {
        // One slow handler must not be able to spend the node's WHOLE drain budget on the wait
        // for it: the steps that follow the wait (OnClosedAsync, the pipe/socket teardown) run on
        // the same budget, and if the two numbers are equal they get nothing at all.
        TimeSpan[] budgets =
        [
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromHours(1),
        ];

        foreach (var budget in budgets)
        {
            var bound = TcpConnectionLifecycle.HandlerLoopTimeoutFor(budget);
            await Assert
                .That(bound)
                .IsLessThan(budget)
                .Because(
                    $"a {budget} drain budget has to leave room for the close steps after the wait"
                );
            await Assert
                .That(bound)
                .IsGreaterThan(TimeSpan.Zero)
                .Because($"a {budget} drain budget still bounds the wait");
        }

        // DrainTimeout.Zero is "no deadline at all" (StopAsync creates no stop CTS), so any
        // finite bound is strictly inside it.
        var unbounded = TcpConnectionLifecycle.HandlerLoopTimeoutFor(TimeSpan.Zero);
        await Assert.That(unbounded).IsGreaterThan(TimeSpan.Zero);

        var defaults = new TcpNodeOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionHandler = new NoOpTcpHandler(),
        };
        await Assert
            .That(TcpConnectionLifecycle.HandlerLoopTimeoutFor(defaults.DrainTimeout))
            .IsLessThan(defaults.DrainTimeout)
            .Because("the default node must not sit on the 5 s == 5 s collision");
    }

    [Test]
    public async Task StopAsync_gives_up_on_a_parked_handler_loop_inside_the_drain_budget()
    {
        var logger = new SpyLogger();
        var handler = new ParkedConnectHandler();
        // 500 ms budget: the handler-loop wait is half of it, so the close (and through it the
        // drain) is free well before the deadline instead of after the old fixed 5 s.
        var node = CreateNode(handler, logger, drainTimeout: TimeSpan.FromMilliseconds(500));
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );

        await node.StartAsync();
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.Connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        try
        {
            var stopTask = node.StopAsync();

            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

            await Assert.That(closed.Reason).IsEqualTo(TcpCloseReason.NodeStopping);
            await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
            await Assert
                .That(
                    logger.Calls.Any(x =>
                        x.EventId.Id == (int)NodeFaultCode.StopFailed
                        && (
                            x.Message?.Contains("tcp.close.wait", StringComparison.Ordinal) ?? false
                        )
                    )
                )
                .IsTrue()
                .Because("giving up on the handler loop is reported, not silent");
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    [Test]
    public async Task TryTrackConnection_returns_false_when_node_is_stopping()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode(null);
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
            await using var node = CreateNode(null);
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

        // The monitor wakes at min(IdleTimeout, IdleScanInterval) = 150 ms, so the
        // close is expected well before the 5 s scan interval. Wait with a generous
        // margin (still below that interval, so a regression to using the scan
        // interval would time out): the previous fixed 1 s margin was flaky on
        // loaded CI runners.
        var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(4));

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
        var logger = new SpyLogger();
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode(logger);
            typeof(TcpNode)
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(node, NodeState.Stopping);

            await InvokeProcessAcceptedSocketAsync(node, pair.Server);

            await Assert.That(logger.Calls.Count).IsEqualTo(1);
            await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
            await Assert.That(call!.Level).IsEqualTo(LogLevel.Warning);
            await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.SessionRejected);
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

    private static TcpNode CreateNode(ILogger? logger) => CreateNode(new NoOpTcpHandler(), logger);

    private static TcpNode CreateNode(
        ITcpConnectionHandler handler,
        ILogger? logger = null,
        IPEndPoint? endpoint = null,
        bool enableDualMode = false,
        TimeSpan? idleTimeout = null,
        TimeSpan? idleScanInterval = null,
        TimeSpan? drainTimeout = null
    )
    {
        var options = new TcpNodeOptions
        {
            Endpoint = endpoint ?? new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionHandler = handler,
            Logger = logger,
            EnableDualMode = enableDualMode,
            IdleTimeout = idleTimeout ?? TimeSpan.Zero,
            IdleScanInterval = idleScanInterval ?? TimeSpan.FromSeconds(1),
        };
        if (drainTimeout is { } timeout)
        {
            options.DrainTimeout = timeout;
        }

        return new TcpNode(options);
    }

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

    /// <summary>
    /// Handler whose connect callback never returns: the connection's handler loop stays live for
    /// the whole test, which is exactly what the bounded wait for a handler loop exists for. The
    /// park is never released — releasing it would run the loop against a pipe the test already
    /// tore down, and an unobserved fault there would be noise, not a result.
    /// </summary>
    private sealed class ParkedConnectHandler : ITcpConnectionHandler
    {
        private readonly TaskCompletionSource _park = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ClosedTcpConnection> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            Connected.TrySetResult();
            return _park.Task;
        }

        public ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(buffer.End);

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTcpHandler : ITcpConnectionHandler
    {
        private readonly bool _blockClose;
        private readonly TaskCompletionSource _allowClose = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

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

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            CloseStarted.TrySetResult();

            if (_blockClose)
            {
                return new ValueTask(WaitForCloseAsync(reason, error, cancellationToken));
            }

            Closed.TrySetResult(new ClosedTcpConnection(reason, error));
            return ValueTask.CompletedTask;
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

    [Test]
    public async Task Constructor_rejects_null_connection_handler()
    {
        await Assert
            .That(() =>
                new TcpNode(
                    new TcpNodeOptions
                    {
                        Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                        ConnectionHandler = null!,
                    }
                )
            )
            .Throws<ArgumentNullException>();
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

        public ValueTask OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }

    private sealed record ConnectedTcpConnection(long ConnectionId, EndPoint RemoteEndPoint);

    private sealed record ClosedTcpConnection(TcpCloseReason Reason, Exception? Error);
}
