namespace PicoNode.Tests;

public sealed class UdpNodeBranchTests
{
    [Test]
    public async Task Constructor_rejects_invalid_dispatch_worker_count()
    {
        await Assert
            .That(
                () =>
                    new UdpNode(
                        new UdpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            DatagramHandler = new NoOpUdpHandler(),
                            DispatchWorkerCount = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_rejects_invalid_buffer_and_queue_settings()
    {
        await Assert
            .That(
                () =>
                    new UdpNode(
                        new UdpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            DatagramHandler = new NoOpUdpHandler(),
                            DatagramQueueCapacity = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new UdpNode(
                        new UdpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            DatagramHandler = new NoOpUdpHandler(),
                            ReceiveSocketBufferSize = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new UdpNode(
                        new UdpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            DatagramHandler = new NoOpUdpHandler(),
                            SendSocketBufferSize = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new UdpNode(
                        new UdpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            DatagramHandler = new NoOpUdpHandler(),
                            ReceiveDatagramBufferSize = 0,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(
                () =>
                    new UdpNode(
                        new UdpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            DatagramHandler = new NoOpUdpHandler(),
                            ReceiveDatagramBufferSize = 65528,
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_rejects_negative_receive_fault_backoff()
    {
        await Assert
            .That(
                () =>
                    new UdpNode(
                        new UdpNodeOptions
                        {
                            Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                            DatagramHandler = new NoOpUdpHandler(),
                            ReceiveFaultBackoff = TimeSpan.FromMilliseconds(-1),
                        }
                    )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ReportFault_returns_when_handler_is_null()
    {
        var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new NoOpUdpHandler(),
            }
        );

        InvokeReportFault(
            node,
            NodeFaultCode.DatagramReceiveFailed,
            "udp.receive",
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
            NodeFaultCode.DatagramHandlerFailed,
            "udp.datagram.handler",
            new SocketException((int)SocketError.NetworkDown)
        );

        await Assert.That(logger.Calls.Count).IsEqualTo(1);
        await node.DisposeAsync();
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
    public async Task StartAsync_reports_start_failed_when_bind_throws()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        using var blocker = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );
        blocker.Bind(endpoint);

        var logger = new SpyLogger();
        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = (IPEndPoint)blocker.LocalEndPoint!,
                DatagramHandler = new NoOpUdpHandler(),
                Logger = logger,
            }
        );

        var exception = await Assert.That(() => node.StartAsync()).Throws<SocketException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(logger.Calls.Count).IsEqualTo(1);
        await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
        await Assert.That(call!.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.StartFailed);
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    private static UdpNode CreateNode(ILogger? logger) =>
        new(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                DatagramHandler = new NoOpUdpHandler(),
                Logger = logger,
            }
        );

    private static void InvokeReportFault(
        UdpNode node,
        NodeFaultCode code,
        string operation,
        Exception? exception
    )
    {
        var method = typeof(UdpNode).GetMethod(
            "ReportFault",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        method.Invoke(node, [code, operation, exception]);
    }

    private sealed class NoOpUdpHandler : IUdpDatagramHandler
    {
        public Task OnDatagramAsync(
            IUdpDatagramContext context,
            ArraySegment<byte> datagram,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
