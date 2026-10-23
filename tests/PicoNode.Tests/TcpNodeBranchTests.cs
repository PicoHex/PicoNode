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
    public async Task DisposeAsync_waits_for_a_still_running_loop_before_releasing_its_members()
    {
        // A stop abandons its background-loop waits when DrainTimeout expires: ObserveBackgroundLoopAsync
        // returns on the OperationCanceledException while the loop is still running, so "the stop
        // returned" does not mean "the loops exited". The loop task is replaced with a GATED stand-in so
        // that "a loop which has not exited yet" is a certainty rather than a race — the dispose path
        // holds the Task, not the loop body, and cannot tell the two apart.
        var node = CreateNode(new NoOpTcpHandler(), drainTimeout: TimeSpan.FromMilliseconds(1));
        var cts = GetNodeCancellation(node);
        var pool = GetEventArgsPool(node);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? memberFault = null;
        var standIn = Task.Run(async () =>
        {
            entered.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            try
            {
                // What a real loop does with the members the dispose used to release underneath it: the
                // accept loop's fault pause reads _cts.Token and the loop's first act is a Rent().
                _ = cts.Token;
                _ = pool.Rent();
            }
            catch (Exception ex)
            {
                memberFault = ex;
            }
        });

        await node.StartAsync();
        var realAcceptLoop = ReplaceAcceptTask(node, standIn);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = node.DisposeAsync().AsTask();

        await Assert
            .That(await HasCompletedWithinAsync(dispose, TimeSpan.FromMilliseconds(250)))
            .IsFalse()
            .Because("the dispose has to wait for the loop, not release what it is still using");

        gate.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert
            .That(memberFault)
            .IsNull()
            .Because(
                "an ObjectDisposedException here is exactly the unobserved task exception the ordering removes"
            );
        await Assert.That(standIn.IsCompletedSuccessfully).IsTrue();
        await Assert.That(node.State).IsEqualTo(NodeState.Disposed);

        // Proof the wait did not turn into a leak: once the loop was gone, its members WERE released.
        await Assert.That(() => pool.Rent()).Throws<ObjectDisposedException>();

        await realAcceptLoop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task DisposeAsync_cancels_the_node_signal_even_when_the_stop_was_skipped()
    {
        // The bounded wait in DisposeAsync can only end early because the loops OBSERVE a cancellation, so
        // the cancel has to be unconditional rather than inherited from the stop. StopAsync returns at once
        // for a node it considers already stopped - and the state is not always the stop's own doing (a
        // repeat stop after a drained one returns without touching the signal; the concurrent-stop tests
        // force the state outright). A dispose that relied on the stop would then sit out the whole wait and
        // report a loop that never exited.
        var node = CreateNode(new NoOpTcpHandler());
        var faults = new List<NodeFault>();
        node.OnFault += faults.Add;

        await node.StartAsync();
        typeof(TcpNode)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(node, NodeState.Stopped);

        var completed = await HasCompletedWithinAsync(
            node.DisposeAsync().AsTask(),
            TimeSpan.FromSeconds(1)
        );

        await Assert
            .That(completed)
            .IsTrue()
            .Because("the accept loop has to be cancelled out of its wait, not waited out");
        await Assert
            .That(faults.Count)
            .IsEqualTo(0)
            .Because("nothing had to be abandoned, so nothing is reported");
        await Assert.That(node.State).IsEqualTo(NodeState.Disposed);
    }

    [Test]
    public async Task DisposeAsync_does_not_release_members_a_loop_is_still_running()
    {
        // The other half of the same rule: when the bounded wait expires the members are NOT released.
        // A disposed source is precisely what the still-running loop would then hit, and giving up is
        // reported instead of passing silently.
        var node = CreateNode(new NoOpTcpHandler(), drainTimeout: TimeSpan.FromMilliseconds(1));
        Exception? giveUp = null;
        node.OnFault += fault =>
        {
            if (fault.Code == NodeFaultCode.StopFailed)
            {
                giveUp = fault.Exception;
            }
        };

        var cts = GetNodeCancellation(node);
        var pool = GetEventArgsPool(node);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var standIn = Task.Run(async () =>
        {
            entered.TrySetResult();
            await gate.Task.ConfigureAwait(false);
        });

        await node.StartAsync();
        var realAcceptLoop = ReplaceAcceptTask(node, standIn);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await node.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(node.State).IsEqualTo(NodeState.Disposed);
        await Assert
            .That(giveUp)
            .IsAssignableTo<TimeoutException>()
            .Because(
                "a loop that outlived the bounded wait is a reported give-up, not a silent one"
            );

        // Still usable: the loop that is still running is the reason they were left alone.
        Exception? memberFault = null;
        try
        {
            _ = cts.Token;
            _ = pool.Rent();
        }
        catch (Exception ex)
        {
            memberFault = ex;
        }

        await Assert
            .That(memberFault)
            .IsNull()
            .Because(
                "releasing a member a live loop still reads is the ObjectDisposedException to avoid"
            );

        gate.TrySetResult();
        await standIn.WaitAsync(TimeSpan.FromSeconds(5));
        await realAcceptLoop.WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task DrainBudget_Cap_only_trims_the_upper_end()
    {
        // The timer limit used to be re-derived at every reader (four copies of
        // `value < MaxTimerDuration ? value : MaxTimerDuration`). DrainBudget.Cap is that expression's one
        // home, and its contract is deliberately narrow: it is an UPPER bound only. Values at or below the
        // limit pass through untouched - including TimeSpan.Zero, whose "no deadline at all" meaning is
        // each caller's own business, not the cap's.
        var limit = DrainBudget.MaxTimerDuration;
        await Assert.That(limit).IsGreaterThan(TimeSpan.Zero);

        await Assert.That(DrainBudget.Cap(TimeSpan.Zero)).IsEqualTo(TimeSpan.Zero);
        await Assert
            .That(DrainBudget.Cap(TimeSpan.FromMilliseconds(-1)))
            .IsEqualTo(TimeSpan.FromMilliseconds(-1));
        await Assert
            .That(DrainBudget.Cap(TimeSpan.FromSeconds(5)))
            .IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(DrainBudget.Cap(limit)).IsEqualTo(limit);
        await Assert
            .That(DrainBudget.Cap(limit + TimeSpan.FromTicks(1)))
            .IsEqualTo(limit)
            .Because("one tick past the limit is exactly what the timer APIs reject");
        await Assert.That(DrainBudget.Cap(TimeSpan.MaxValue)).IsEqualTo(limit);
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
    public async Task Zero_accept_backoff_yields_between_faults()
    {
        // A zero AcceptFaultBackoff used to mean "no pause at all": the accept loop's catch did a bare
        // `continue`, so a persistently failing accept (WSAENOBUFS, EMFILE, an exhausted handle table)
        // re-entered the accept wait with no suspension — a pinned core, and every fault logged and
        // raised to every OnFault subscriber as fast as the loop could go round. The option still
        // means "how long to pause"; a value that would switch the pause off is raised to a floor.
        await using var node = CreateNode(
            new NoOpTcpHandler(),
            logger: null,
            acceptFaultBackoff: TimeSpan.Zero
        );

        var probe = await ObserveAcceptFaultsAsync(node, faultsToObserve: 3);

        await AssertAcceptLoopSuspendedAsync(
            probe,
            "a configured zero backoff must not switch the pause off"
        );
    }

    [Test]
    public async Task A_hot_reloaded_zero_accept_backoff_is_clamped_too()
    {
        // The option is hot-reloadable and ApplyConfigReload parses it with TimeSpan.TryParse, which
        // accepts `00:00:00` (and negative values) — so the clamp has to sit where the loop READS the
        // option, not in the constructor, which is the only place that rejects a negative today.
        await using var node = CreateNode(new NoOpTcpHandler(), logger: null);

        var probe = await ObserveAcceptFaultsAsync(
            node,
            faultsToObserve: 3,
            // What ApplyConfigReload assigns for `AcceptFaultBackoff = 00:00:00`.
            afterStart: started => started.Options.AcceptFaultBackoff = TimeSpan.Zero
        );

        await AssertAcceptLoopSuspendedAsync(
            probe,
            "a reloaded zero backoff must be clamped exactly like a configured one"
        );
    }

    [Test]
    public async Task The_accept_fault_backoff_is_clamped_into_a_legal_range()
    {
        // Zero is what the audit measured. A negative value only arrives through a hot reload (the
        // constructor rejects it), and an absurdly large one would make Task.Delay itself throw — which
        // would end the accept loop for good instead of pacing it — so the clamp is two-sided.
        var floor = TcpNode.AcceptFaultBackoffFloor;
        await Assert.That(floor).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(TcpNode.EffectiveAcceptFaultBackoff(TimeSpan.Zero)).IsEqualTo(floor);
        await Assert
            .That(TcpNode.EffectiveAcceptFaultBackoff(TimeSpan.FromMilliseconds(-1)))
            .IsEqualTo(floor);
        await Assert.That(TcpNode.EffectiveAcceptFaultBackoff(TimeSpan.MinValue)).IsEqualTo(floor);

        // A positive value keeps its meaning: the option is "how long to pause", not a suggestion.
        await Assert
            .That(TcpNode.EffectiveAcceptFaultBackoff(TimeSpan.FromMilliseconds(50)))
            .IsEqualTo(TimeSpan.FromMilliseconds(50));
        await Assert
            .That(TcpNode.EffectiveAcceptFaultBackoff(TimeSpan.FromMilliseconds(0.5)))
            .IsEqualTo(TimeSpan.FromMilliseconds(0.5));

        // The upper end is the timer limit, so the pause the loop schedules is always legal.
        var capped = TcpNode.EffectiveAcceptFaultBackoff(TimeSpan.MaxValue);
        await Assert.That(capped).IsEqualTo(DrainBudget.MaxTimerDuration);
        await Assert
            .That(IsLegalWaitTimeout(capped))
            .IsTrue()
            .Because("Task.Delay has to accept the pause the loop derives from the option");
    }

    [Test]
    public async Task A_huge_drain_budget_still_yields_a_legal_handler_loop_bound()
    {
        // The derived bound is handed to Task.WaitAsync verbatim, and WaitAsync rejects a timeout above
        // ~49.7 days (0xFFFFFFFE ms) with ArgumentOutOfRangeException — thrown SYNCHRONOUSLY, inside the
        // try that reads it as "the loop's own failure", which silently skipped the bound altogether.
        // DrainTimeout is reloadable, so a huge value is reachable from configuration.
        TimeSpan[] hugeBudgets =
        [
            TimeSpan.FromDays(90), // half already fits: must stay unclamped
            TimeSpan.FromDays(100),
            TimeSpan.FromDays(365),
            TimeSpan.FromDays(36500),
            TimeSpan.MaxValue,
        ];

        foreach (var budget in hugeBudgets)
        {
            var bound = TcpConnectionLifecycle.HandlerLoopTimeoutFor(budget);
            await Assert
                .That(bound)
                .IsGreaterThan(TimeSpan.Zero)
                .Because($"a {budget} drain budget still bounds the wait");
            await Assert
                .That(bound)
                .IsLessThan(budget)
                .Because($"a {budget} drain budget has to leave room for the close steps after it");
            await Assert.That(bound).IsLessThanOrEqualTo(DrainBudget.MaxTimerDuration);
            await Assert
                .That(IsLegalWaitTimeout(bound))
                .IsTrue()
                .Because($"Task.WaitAsync has to accept the bound derived from a {budget} budget");
        }

        // The cap IS the timer limit — not a smaller arbitrary number — and the derivation is untouched
        // below it, so an ordinary budget keeps exactly the bound it had.
        await Assert
            .That(TcpConnectionLifecycle.HandlerLoopTimeoutFor(TimeSpan.MaxValue))
            .IsEqualTo(DrainBudget.MaxTimerDuration);
        await Assert
            .That(TcpConnectionLifecycle.HandlerLoopTimeoutFor(TimeSpan.FromDays(90)))
            .IsEqualTo(TimeSpan.FromDays(45));
    }

    [Test]
    public async Task A_huge_drain_budget_does_not_skip_the_handler_loop_bound()
    {
        var handler = new ParkedConnectHandler();
        var node = CreateNode(handler, drainTimeout: TimeSpan.FromDays(365));
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
            var connection = GetConnections(node).Values.First();
            // An external close, so CloseCoreAsync has to wait the handler loop out — with the bound
            // derived from a 365-day budget, i.e. the clamped 49.7 days.
            connection.Close(TcpCloseReason.NodeStopping);

            // The regression is unmissable in one direction only: a skipped bound releases the close
            // (OnClosedAsync runs) within microseconds, whereas the correct behaviour is parked on a
            // handler the test owns and cannot complete inside the window.
            var releasedWithoutWaiting = await HasCompletedWithinAsync(
                handler.Closed.Task,
                TimeSpan.FromSeconds(1)
            );
            await Assert
                .That(releasedWithoutWaiting)
                .IsFalse()
                .Because(
                    "a huge DrainTimeout must bound the wait for the handler loop, not skip it"
                );
        }
        finally
        {
            // The 365-day budget used to make StopAsync's own CancelAfter throw as well; T2.6 clamps it
            // once at the boundary, so the teardown no longer needs a small budget — it gets one anyway,
            // because nothing here wants a 24-day bound parked behind the handler.
            node.Options.DrainTimeout = TimeSpan.FromMilliseconds(250);
            await node.DisposeAsync();
        }
    }

    [Test]
    public async Task A_huge_drain_budget_cannot_break_the_stop_path()
    {
        // DrainTimeout accepts any non-negative TimeSpan — a host assigns it directly, and the reload
        // path takes whatever TimeSpan.TryParse accepts, up to TimeSpan.MaxValue — while
        // CancellationTokenSource.CancelAfter throws ArgumentOutOfRangeException above 0xFFFFFFFE ms.
        // That call sat BEFORE StopAsync's own try, so a huge budget did not shorten the drain, it
        // removed it: no connection was asked to close, the node stayed Stopping, and DisposeAsync
        // rethrew after the teardown had already run.
        await Assert.That(DrainBudget.Effective(TimeSpan.Zero)).IsEqualTo(TimeSpan.Zero);
        await Assert
            .That(DrainBudget.Effective(TimeSpan.FromSeconds(5)))
            .IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert
            .That(DrainBudget.Effective(TimeSpan.FromDays(365)))
            .IsEqualTo(DrainBudget.MaxTimerDuration);
        await Assert
            .That(DrainBudget.Effective(TimeSpan.MaxValue))
            .IsEqualTo(DrainBudget.MaxTimerDuration);

        var node = CreateNode(new NoOpTcpHandler(), drainTimeout: TimeSpan.FromDays(365));
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        try
        {
            await node.StartAsync();
            await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
            var connected = await PollUntilAsync(
                () => node.GetMetrics().ActiveConnections == 1,
                TimeSpan.FromSeconds(10)
            );
            await Assert
                .That(connected)
                .IsTrue()
                .Because("the client has to be tracked before the stop begins");

            // The whole stop path: deadline timer, drain, connection close.
            await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(node.State).IsEqualTo(NodeState.Stopped);

            // The drain really ran: the server closed the connection, so the client reads EOF.
            var buffer = new byte[1];
            var read = await client
                .ReceiveAsync(buffer, SocketFlags.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(read).IsEqualTo(0).Because("the drain has to close the connection");
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

    /// <summary>
    /// The observations that together mean "the accept loop suspended between faults", taken on
    /// counters that stopping the loop has already frozen.
    /// </summary>
    /// <remarks>
    /// PRIMARY and deliberately timer-free: the loop cannot report fault N+1 before the pause for
    /// fault N has elapsed, so #backoffs is faults-1 or faults at every instant. A loop that "backs
    /// off" by not waiting keeps #backoffs at 0 while the fault count climbs — the audit's spin,
    /// observed with no clock involved.
    /// <para>
    /// SECONDARY, and the reason the primary is not enough on its own: a "pause" that costs nothing —
    /// <c>Task.Delay(TimeSpan.Zero)</c> completes immediately — would be counted just as faithfully, so
    /// the two &gt;= 1 ms pauses between three faults are also required to have taken at least a
    /// millisecond. That is a lower bound on a wait that provably cannot return early, not a race.
    /// </para>
    /// </remarks>
    private static async Task AssertAcceptLoopSuspendedAsync(
        AcceptFaultProbe probe,
        string expectation
    )
    {
        await Assert.That(probe.Faults).IsGreaterThanOrEqualTo(3);
        await Assert
            .That(probe.Backoffs)
            .IsGreaterThanOrEqualTo(probe.Faults - 1)
            .Because(
                $"every accept fault after the first must be preceded by a completed pause: {expectation}"
            );
        await Assert
            .That(probe.Backoffs)
            .IsLessThanOrEqualTo(probe.Faults)
            .Because("a pause is counted once it elapsed, never ahead of the fault that caused it");
        await Assert
            .That(probe.FirstToNthFault)
            .IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1))
            .Because($"the pause has to be one the loop actually waits out: {expectation}");
    }

    /// <summary>
    /// Breaks the node's listening socket so that every accept from here on fails, and reports what
    /// the accept loop did before <paramref name="faultsToObserve" /> such faults had been raised. The
    /// listener is disposed behind the node's back: it is the one way a test can make
    /// <c>AcceptAsync</c> fail persistently without the node's own stop, which cancels the loop out of
    /// the picture. The loop is stopped before the counters are read, so both of them are final.
    /// </summary>
    private static async Task<AcceptFaultProbe> ObserveAcceptFaultsAsync(
        TcpNode node,
        int faultsToObserve,
        Action<TcpNode>? afterStart = null
    )
    {
        var faults = 0;
        long firstFault = 0;
        long nthFault = 0;
        node.OnFault += fault =>
        {
            if (fault.Code != NodeFaultCode.AcceptFailed)
            {
                return;
            }

            var raised = Interlocked.Increment(ref faults);
            var stamp = Stopwatch.GetTimestamp();
            if (raised == 1)
            {
                Volatile.Write(ref firstFault, stamp);
            }
            else if (raised == faultsToObserve)
            {
                Volatile.Write(ref nthFault, stamp);
            }
        };

        await node.StartAsync();
        afterStart?.Invoke(node);
        GetListener(node).Dispose();

        var observedInTime = await PollUntilAsync(
            () => Volatile.Read(ref faults) >= faultsToObserve,
            TimeSpan.FromSeconds(10)
        );

        // Freeze the loop, and with it both counters, before reading them.
        await node.StopAsync();

        var raisedFaults = Volatile.Read(ref faults);
        await Assert
            .That(observedInTime)
            .IsTrue()
            .Because(
                $"a permanently broken listener should raise {faultsToObserve} accept faults, saw {raisedFaults}"
            );

        return new AcceptFaultProbe(
            raisedFaults,
            node.AcceptFaultBackoffs,
            Stopwatch.GetElapsedTime(Volatile.Read(ref firstFault), Volatile.Read(ref nthFault))
        );
    }

    /// <summary>
    /// Bounded observation of a side effect that has no task to await. The delay is only the poll
    /// interval on a positive observation, never an order the test relies on, and the deadline is on a
    /// <see cref="Stopwatch" /> so a wall clock that steps cannot expire the window early.
    /// </summary>
    private static async Task<bool> PollUntilAsync(Func<bool> probe, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (probe())
            {
                return true;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Bounded observation of "this task completed", used to assert the ABSENCE of an event: the
    /// regression is unblocked and shows up immediately, whereas the correct behaviour is parked on a
    /// seam the test owns and cannot complete inside the window.
    /// </summary>
    private static async Task<bool> HasCompletedWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <see cref="Task.WaitAsync(TimeSpan)" /> accepts the timeout. It validates the value
    /// synchronously — even for an already-completed task, so this needs no wait — and that is exactly
    /// the call the close makes with the derived handler-loop bound.
    /// </summary>
    private static bool IsLegalWaitTimeout(TimeSpan timeout)
    {
        try
        {
            _ = Task.CompletedTask.WaitAsync(timeout);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private sealed record AcceptFaultProbe(long Faults, long Backoffs, TimeSpan FirstToNthFault);

    private static TcpNode CreateNode(ILogger? logger) => CreateNode(new NoOpTcpHandler(), logger);

    private static TcpNode CreateNode(
        ITcpConnectionHandler handler,
        ILogger? logger = null,
        IPEndPoint? endpoint = null,
        bool enableDualMode = false,
        TimeSpan? idleTimeout = null,
        TimeSpan? idleScanInterval = null,
        TimeSpan? drainTimeout = null,
        TimeSpan? acceptFaultBackoff = null
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

        if (acceptFaultBackoff is { } backoff)
        {
            options.AcceptFaultBackoff = backoff;
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

    private static CancellationTokenSource GetNodeCancellation(TcpNode node) =>
        (CancellationTokenSource)
            typeof(TcpNode)
                .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(node)!;

    private static SocketIoEventArgsPool GetEventArgsPool(TcpNode node) =>
        (SocketIoEventArgsPool)
            typeof(TcpNode)
                .GetField("_eventArgsPool", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(node)!;

    /// <summary>
    /// Swaps the accept-loop TASK for a stand-in and returns the task it replaced, so the test can still
    /// observe the real loop. The node only ever holds the Task, so a dispose path cannot tell a gated
    /// stand-in from a loop that has not finished yet — which is the state the ordering has to be
    /// correct for.
    /// </summary>
    private static Task ReplaceAcceptTask(TcpNode node, Task replacement)
    {
        var field = typeof(TcpNode).GetField(
            "_acceptTask",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        var previous = (Task?)field.GetValue(node);
        field.SetValue(node, replacement);
        return previous ?? Task.CompletedTask;
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
