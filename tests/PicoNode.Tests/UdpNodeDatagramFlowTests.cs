namespace PicoNode.Tests;

public sealed class UdpNodeDatagramFlowTests
{
    [Test]
    public async Task Received_datagram_context_has_nonzero_connection_id()
    {
        var handler = new CapturingUdpHandler();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = handler,
            }
        );

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await node.StartAsync();

        await client.SendToAsync(
            new byte[] { 1, 2, 3, 4 },
            SocketFlags.None,
            (IPEndPoint)node.LocalEndPoint
        );

        var received = await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(received.Context.ConnectionId).IsGreaterThan(0);
    }

    [Test]
    public async Task SendAsync_accepts_ReadOnlyMemory()
    {
        var handler = new CapturingUdpHandler();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = handler,
            }
        );

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await node.StartAsync();

        var serverEndpoint = (IPEndPoint)node.LocalEndPoint;
        var reply = new byte[] { 9, 8, 7 };
        handler.SetReply(reply);

        await client.SendToAsync(new byte[] { 1, 2, 3, 4 }, SocketFlags.None, serverEndpoint);

        var responseBuffer = new byte[reply.Length];
        var result = await client
            .ReceiveFromAsync(responseBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0))
            .WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.That(result.ReceivedBytes).IsEqualTo(reply.Length);
        await Assert.That(responseBuffer).IsEquivalentTo(reply);
    }

    [Test]
    public async Task StopAsync_completes_within_DrainTimeout_when_handler_ignores_cancellation()
    {
        var handler = new UnCancellableBlockingHandler();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = handler,
                DrainTimeout = TimeSpan.FromMilliseconds(500),
            }
        );

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await node.StartAsync();

        await client.SendToAsync(
            new byte[] { 1, 2, 3, 4 },
            SocketFlags.None,
            (IPEndPoint)node.LocalEndPoint
        );

        // Wait until the handler is invoked (and blocked).
        await handler.FirstInvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var stopTask = node.StopAsync();

        // StopAsync must complete within a reasonable bound of DrainTimeout,
        // even though the handler ignores cancellation.
        var delay = Task.Delay(TimeSpan.FromSeconds(5));
        await Task.WhenAny(stopTask, delay);

        handler.Release();

        await Assert.That(stopTask.IsCompleted).IsTrue();
        await Assert.That(delay.IsCompleted).IsFalse();
    }

    [Test]
    public async Task Received_datagram_exposes_remote_endpoint_and_allows_reply()
    {
        var handler = new CapturingUdpHandler();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = handler,
            }
        );

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await node.StartAsync();

        var serverEndpoint = (IPEndPoint)node.LocalEndPoint;
        var payload = new byte[] { 1, 2, 3, 4 };
        var reply = new byte[] { 9, 8, 7 };
        handler.SetReply(reply);

        await client.SendToAsync(payload, SocketFlags.None, serverEndpoint);

        var received = await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var responseBuffer = new byte[reply.Length];
        var result = await client
            .ReceiveFromAsync(responseBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0))
            .WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.That(received.RemoteEndPoint).IsEqualTo((IPEndPoint)client.LocalEndPoint!);
        await Assert.That(received.Datagram).IsEquivalentTo(payload);
        await Assert.That(result.ReceivedBytes).IsEqualTo(reply.Length);
        await Assert.That(responseBuffer).IsEquivalentTo(reply);
        await Assert.That((IPEndPoint)result.RemoteEndPoint).IsEqualTo(serverEndpoint);
    }

    [Test]
    public async Task Handler_exception_reports_datagram_handler_failed_fault()
    {
        var logger = new SpyLogger();
        var handler = new ThrowingUdpHandler();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = handler,
                Logger = logger,
            }
        );

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await node.StartAsync();
        await client.SendToAsync(
            new byte[] { 42 },
            SocketFlags.None,
            (IPEndPoint)node.LocalEndPoint
        );

        await Task.Delay(500);

        await Assert.That(logger.Calls.Count).IsEqualTo(1);
        await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
        await Assert.That(call!.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.DatagramHandlerFailed);
        await Assert.That(call.Exception).IsTypeOf<InvalidOperationException>();
    }

    [Test]
    public async Task Drop_newest_reports_datagram_dropped_when_queue_is_full()
    {
        var logger = new SpyLogger();
        var dropReported = new TaskCompletionSource<LogCall>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var handler = new BlockingUdpHandler();
        var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = handler,
                Logger = logger,
                DispatchWorkerCount = 1,
                DatagramQueueCapacity = 1,
                QueueOverflowMode = UdpOverflowMode.DropNewest,
            }
        );

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        try
        {
            await node.StartAsync();

            var serverEndpoint = (IPEndPoint)node.LocalEndPoint;
            await client.SendToAsync(new byte[] { 1 }, SocketFlags.None, serverEndpoint);
            await handler.FirstInvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            for (var i = 0; i < 8; i++)
            {
                await client.SendToAsync(
                    new byte[] { (byte)(i + 2) },
                    SocketFlags.None,
                    serverEndpoint
                );
            }

            await Task.Delay(500);

            await Assert
                .That(logger.Calls.Any(x => x.EventId.Id == (int)NodeFaultCode.DatagramDropped))
                .IsTrue();
        }
        finally
        {
            handler.Release();
            await node.DisposeAsync();
        }
    }

    [Test]
    public async Task Context_send_reports_send_failed_when_node_is_disposed()
    {
        var logger = new SpyLogger();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new CapturingUdpHandler(),
                Logger = logger,
            }
        );
        var context = new UdpDatagramContext(node, 1, new IPEndPoint(IPAddress.Loopback, 45678));

        await node.DisposeAsync();

        var exception = await Assert
            .That(() => context.SendAsync(new ArraySegment<byte>(new byte[] { 5, 6, 7 })))
            .Throws<ObjectDisposedException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(logger.Calls.Count).IsEqualTo(1);
        await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
        await Assert.That(call!.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.SendFailed);
    }

    [Test]
    public async Task Receive_loop_reports_datagram_receive_failed_when_socket_is_disposed_unexpectedly()
    {
        var logger = new SpyLogger();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new CapturingUdpHandler(),
                Logger = logger,
            }
        );

        await node.StartAsync();

        var socket = (Socket)
            typeof(UdpNode)
                .GetField(
                    "_socket",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                )!
                .GetValue(node)!;
        socket.Dispose();

        await Task.Delay(500);

        await Assert
            .That(logger.Calls.Any(x => x.EventId.Id == (int)NodeFaultCode.DatagramReceiveFailed))
            .IsTrue();
    }

    [Test]
    public async Task StopAsync_drains_queued_datagrams()
    {
        // INode.StopAsync is documented as "draining in-flight work up to the configured drain
        // timeout", but the dispatch workers used to run on the RECEIVE lifetime: StopAsync cancelled
        // it first, so a handler that respects its token threw OperationCanceledException, the worker's
        // catch discarded the rest of the queue, and every queued datagram was aborted instead of
        // delivered.
        var handler = new GatedUdpHandler();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = handler,
                DispatchWorkerCount = 1,
                DatagramQueueCapacity = 16,
                DrainTimeout = TimeSpan.FromSeconds(5),
            }
        );

        await node.StartAsync();

        // Feed the dispatch queue directly so that "queued when the stop began" is not a race with the
        // socket: the single worker parks inside the handler on the FIRST datagram (on a gate this test
        // owns, not on a delay), which guarantees the other nine are still in the channel.
        var queue = GetDatagramQueue(node, worker: 0);
        await queue.Writer.WriteAsync(RentLease(1));
        await handler.FirstInvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (byte payload = 2; payload <= 10; payload++)
        {
            await queue.Writer.WriteAsync(RentLease(payload));
        }

        var stopTask = node.StopAsync();

        // The stop must have begun before the handlers are released, or "drained during a stop" is not
        // what this test measured. A positive observation with a deadline, never a sleep.
        var stopping = await PollUntilAsync(
            () => node.State == NodeState.Stopping,
            TimeSpan.FromSeconds(5)
        );
        await Assert.That(stopping).IsTrue().Because("StopAsync has to enter Stopping");

        handler.Release();

        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert
            .That(handler.Invocations)
            .IsEqualTo(10)
            .Because("every datagram queued before the stop has to reach the handler");
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    [Test]
    public async Task A_huge_drain_budget_cannot_break_the_udp_stop_path()
    {
        // DrainTimeout is a plain mutable property and hot-reloadable (TimeSpan.TryParse accepts
        // TimeSpan.MaxValue), while CancellationTokenSource.CancelAfter rejects everything above
        // 0xFFFFFFFE ms — and that call sits before StopAsync's own try, so a huge budget did not
        // shorten the drain, it removed it.
        var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new CapturingUdpHandler(),
                DrainTimeout = TimeSpan.FromDays(365),
            }
        );

        await node.StartAsync();
        await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);

        // DisposeAsync must not rethrow a stop that already tore the node down.
        await node.DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_waits_for_a_still_running_receive_loop_before_releasing_the_intake_lifetime()
    {
        // Same rule as the TCP node, on the intake lifetime: a stop whose drain deadline expires leaves
        // the receive loop's task abandoned (WaitAsync gives up while the task still runs), so "the stop
        // returned" does not mean "the loop exited". The receive task is replaced with a GATED stand-in
        // so that a loop which has not exited yet is a certainty rather than a race.
        var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new CapturingUdpHandler(),
                DrainTimeout = TimeSpan.FromMilliseconds(1),
            }
        );
        var intake = GetReceiveCancellation(node);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? memberFault = null;
        var standIn = Task.Run(async () =>
        {
            entered.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            try
            {
                // What the real loop's fault pause does with the source the dispose used to release
                // underneath it: reading Token on a disposed source throws.
                _ = intake.Token;
            }
            catch (Exception ex)
            {
                memberFault = ex;
            }
        });

        await node.StartAsync();
        var realReceiveLoop = ReplaceReceiveTask(node, standIn);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = node.DisposeAsync().AsTask();

        await Assert
            .That(await CompletesWithinAsync(dispose, TimeSpan.FromMilliseconds(250)))
            .IsFalse()
            .Because(
                "the dispose has to wait for the receive loop, not release what it is still using"
            );

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

        // Proof the wait did not turn into a leak: once the loop was gone, its lifetime WAS released.
        await Assert.That(() => _ = intake.Token).Throws<ObjectDisposedException>();

        await realReceiveLoop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_datagram_that_arrives_after_its_dispatch_queue_closed_is_counted_dropped()
    {
        // The window UdpOverflowMode.Wait leaves open, and StopAsync opens it by design: the receive loop
        // is the only queue WRITER, and the stop completes the queues. A datagram received just before
        // that point makes WriteAsync throw ChannelClosedException, which the generic catch reported as a
        // receive FAILURE - a spurious DatagramReceiveFailed fault, with the datagram counted received but
        // neither delivered nor dropped. The queue is completed here from the test (the same seam, minus
        // the stop) so the window is hit every run instead of once in a while.
        var receiveFailures = 0;
        var dropped = 0;
        var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new CapturingUdpHandler(),
                QueueOverflowMode = UdpOverflowMode.Wait,
                DispatchWorkerCount = 1,
            }
        );
        node.OnFault += fault =>
        {
            if (fault.Code == NodeFaultCode.DatagramReceiveFailed)
            {
                Interlocked.Increment(ref receiveFailures);
            }
            else if (fault.Code == NodeFaultCode.DatagramDropped)
            {
                Interlocked.Increment(ref dropped);
            }
        };

        await node.StartAsync();
        GetDatagramQueue(node, 0).Writer.TryComplete();

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var server = (IPEndPoint)node.LocalEndPoint;

        await client.SendToAsync(new byte[] { 1 }, SocketFlags.None, server);

        var settled = await PollUntilAsync(
            () => Volatile.Read(ref receiveFailures) > 0 || Volatile.Read(ref dropped) > 0,
            TimeSpan.FromSeconds(10)
        );
        await Assert
            .That(settled)
            .IsTrue()
            .Because("the receive loop has to dispose of the datagram one way or the other");

        await Assert
            .That(Volatile.Read(ref receiveFailures))
            .IsEqualTo(0)
            .Because("a queue closed under the write is a deliberate drop, not a receive failure");
        await Assert
            .That(Volatile.Read(ref dropped))
            .IsEqualTo(1)
            .Because("the datagram is dropped, so it has to be counted as dropped");
        await Assert.That(node.GetMetrics().TotalDatagramsReceived).IsEqualTo(1);
        await Assert
            .That(node.GetMetrics().TotalDatagramsDropped)
            .IsEqualTo(1)
            .Because("counted received AND dropped - never neither");

        // A closed queue must not end the loop: the next datagram is still received (and dropped).
        await client.SendToAsync(new byte[] { 2 }, SocketFlags.None, server);
        var second = await PollUntilAsync(
            () => node.GetMetrics().TotalDatagramsReceived == 2,
            TimeSpan.FromSeconds(10)
        );
        await Assert
            .That(second)
            .IsTrue()
            .Because("a completed dispatch queue is not a reason to leave the socket unread");

        await node.DisposeAsync();
    }

    [Test]
    public async Task A_zero_receive_fault_backoff_cannot_spin_the_receive_loop()
    {
        // The same bug T2.5 fixed on the TCP accept loop, in the sibling node: a non-positive backoff
        // made Task.Delay return an already-completed task, so a permanently failing receive re-entered
        // the receive with NO suspension — a pinned core, plus one DatagramReceiveFailed fault logged
        // and raised to every subscriber per iteration, for as long as the fault lasted.
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new CapturingUdpHandler(),
                ReceiveFaultBackoff = TimeSpan.Zero,
            }
        );

        var probe = await ObserveReceiveFaultsAsync(node, faultsToObserve: 3);

        await AssertReceiveLoopSuspendedAsync(
            probe,
            "a configured zero backoff must not switch the pause off"
        );
    }

    [Test]
    public async Task The_receive_fault_backoff_is_clamped_into_a_legal_range()
    {
        // Zero is the value the audit measured. A negative one only arrives through a hot reload (the
        // constructor rejects it), and an absurdly large one would make Task.Delay itself throw — out of
        // the receive loop's catch, ending the loop for good instead of pacing it.
        var floor = UdpNode.ReceiveFaultBackoffFloor;
        await Assert.That(floor).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(UdpNode.EffectiveReceiveFaultBackoff(TimeSpan.Zero)).IsEqualTo(floor);
        await Assert
            .That(UdpNode.EffectiveReceiveFaultBackoff(TimeSpan.FromMilliseconds(-1)))
            .IsEqualTo(floor);
        await Assert.That(UdpNode.EffectiveReceiveFaultBackoff(TimeSpan.MinValue)).IsEqualTo(floor);

        // A positive value keeps its meaning: the option is "how long to pause", not a suggestion.
        await Assert
            .That(UdpNode.EffectiveReceiveFaultBackoff(TimeSpan.FromMilliseconds(50)))
            .IsEqualTo(TimeSpan.FromMilliseconds(50));
        await Assert
            .That(UdpNode.EffectiveReceiveFaultBackoff(TimeSpan.FromMilliseconds(0.5)))
            .IsEqualTo(TimeSpan.FromMilliseconds(0.5));

        // The upper end is the timer limit, so the pause the loop schedules is always legal.
        await Assert
            .That(UdpNode.EffectiveReceiveFaultBackoff(TimeSpan.MaxValue))
            .IsEqualTo(DrainBudget.MaxTimerDuration);
    }

    /// <summary>
    /// Drives the datagram receive loop into a permanent fault — the socket is disposed underneath it,
    /// the same seam the unexpected-disposal test above uses — observes
    /// <paramref name="faultsToObserve"/> <see cref="NodeFaultCode.DatagramReceiveFailed"/> faults,
    /// then STOPS the loop and reports both counters plus the elapsed time between the first and the
    /// last observed fault. The counters are read after the loop has exited, so they are final.
    /// </summary>
    private static async Task<ReceiveFaultProbe> ObserveReceiveFaultsAsync(
        UdpNode node,
        int faultsToObserve
    )
    {
        var faults = 0;
        long firstFault = 0;
        long nthFault = 0;
        node.OnFault += fault =>
        {
            if (fault.Code != NodeFaultCode.DatagramReceiveFailed)
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
        GetSocket(node).Dispose();

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
                $"a permanently broken socket should raise {faultsToObserve} receive faults, saw {raisedFaults}"
            );

        return new ReceiveFaultProbe(
            raisedFaults,
            node.ReceiveFaultBackoffs,
            Stopwatch.GetElapsedTime(Volatile.Read(ref firstFault), Volatile.Read(ref nthFault))
        );
    }

    /// <summary>
    /// The invariant the counter pair carries: a fault is reported BEFORE its pause and the pause is
    /// counted only after it elapsed, so at every instant <c>backoffs ∈ {faults - 1, faults}</c>. The
    /// elapsed lower bound is the secondary observation, and it is not decoration: a "pause" that costs
    /// nothing (<c>Task.Delay(TimeSpan.Zero)</c> returns an already-completed task) would be counted just
    /// as faithfully, so only the elapsed time sees a floor of zero.
    /// </summary>
    private static async Task AssertReceiveLoopSuspendedAsync(
        ReceiveFaultProbe probe,
        string expectation
    )
    {
        await Assert.That(probe.Faults).IsGreaterThanOrEqualTo(3);
        await Assert
            .That(probe.Backoffs)
            .IsGreaterThanOrEqualTo(probe.Faults - 1)
            .Because(
                $"every receive fault after the first must be preceded by a completed pause: {expectation}"
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
    /// Bounded observation of a side effect that has no task to await. The delay is only the poll
    /// interval on a positive observation, never an order the test relies on.
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
    /// Bounded observation of "this task completed". The regression shows up immediately, whereas the
    /// correct behaviour is parked on a gate the test owns and cannot complete inside the window.
    /// </summary>
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
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

    private static CancellationTokenSource GetReceiveCancellation(UdpNode node) =>
        (CancellationTokenSource)
            typeof(UdpNode)
                .GetField("_receiveCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(node)!;

    /// <summary>
    /// Swaps the receive-loop TASK for a stand-in and returns the task it replaced. The node only ever
    /// holds the Task, so a dispose path cannot tell a gated stand-in from a loop that has not finished
    /// yet - which is the state the ordering has to be correct for.
    /// </summary>
    private static Task ReplaceReceiveTask(UdpNode node, Task replacement)
    {
        var field = typeof(UdpNode).GetField(
            "_receiveTask",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        var previous = (Task?)field.GetValue(node);
        field.SetValue(node, replacement);
        return previous ?? Task.CompletedTask;
    }

    private static Channel<UdpDatagramLease> GetDatagramQueue(UdpNode node, int worker)
    {
        var field = typeof(UdpNode).GetField(
            "_queues",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        return ((Channel<UdpDatagramLease>[])field.GetValue(node)!)[worker];
    }

    private static UdpDatagramLease RentLease(byte payload)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1);
        buffer[0] = payload;
        return new UdpDatagramLease(buffer, 1, new IPEndPoint(IPAddress.Loopback, 45678));
    }

    private static Socket GetSocket(UdpNode node) =>
        (Socket)
            typeof(UdpNode)
                .GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(node)!;

    private sealed record ReceiveFaultProbe(long Faults, long Backoffs, TimeSpan FirstToNthFault);

    /// <summary>
    /// Cancellation-aware handler that parks its FIRST invocation on a gate the test owns (no delay,
    /// no sleep) and then does 5 ms of work per datagram. A handler lifetime that is cancelled early
    /// shows up as a lower <see cref="Invocations"/> count rather than as a timing difference.
    /// </summary>
    private sealed class GatedUdpHandler : IUdpDatagramHandler
    {
        private readonly TaskCompletionSource _gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _invocations;

        public TaskCompletionSource FirstInvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Invocations => Volatile.Read(ref _invocations);

        public async ValueTask OnDatagramAsync(
            IUdpDatagramContext context,
            ReadOnlyMemory<byte> datagram,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _invocations);
            FirstInvocationStarted.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }

        public void Release() => _gate.TrySetResult();
    }

    private sealed class CapturingUdpHandler : IUdpDatagramHandler
    {
        private byte[] _reply = [];

        public TaskCompletionSource<ReceivedUdpDatagram> Received { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetReply(byte[] reply)
        {
            _reply = reply;
        }

        public async ValueTask OnDatagramAsync(
            IUdpDatagramContext context,
            ReadOnlyMemory<byte> datagram,
            CancellationToken cancellationToken
        )
        {
            var copy = datagram.ToArray();
            Received.TrySetResult(new ReceivedUdpDatagram(context.RemoteEndPoint, copy, context));

            if (_reply.Length > 0)
            {
                await context.SendAsync(new ReadOnlyMemory<byte>(_reply), cancellationToken);
            }
        }
    }

    private sealed class ThrowingUdpHandler : IUdpDatagramHandler
    {
        public TaskCompletionSource<Exception> ExceptionObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OnDatagramAsync(
            IUdpDatagramContext context,
            ReadOnlyMemory<byte> datagram,
            CancellationToken cancellationToken
        )
        {
            var exception = new InvalidOperationException("handler boom");
            ExceptionObserved.TrySetResult(exception);
            return new ValueTask(Task.FromException(exception));
        }
    }

    private sealed class BlockingUdpHandler : IUdpDatagramHandler
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource FirstInvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask OnDatagramAsync(
            IUdpDatagramContext context,
            ReadOnlyMemory<byte> datagram,
            CancellationToken cancellationToken
        )
        {
            FirstInvocationStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    /// <summary>
    /// Handler that blocks indefinitely and ignores the cancellation token,
    /// simulating a stuck handler that cannot be cancelled.
    /// </summary>
    private sealed class UnCancellableBlockingHandler : IUdpDatagramHandler
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource FirstInvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask OnDatagramAsync(
            IUdpDatagramContext context,
            ReadOnlyMemory<byte> datagram,
            CancellationToken cancellationToken
        )
        {
            FirstInvocationStarted.TrySetResult();
            // Intentionally ignore cancellationToken — block until released.
            await _release.Task;
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed record ReceivedUdpDatagram(
        EndPoint RemoteEndPoint,
        byte[] Datagram,
        IUdpDatagramContext Context
    );
}
