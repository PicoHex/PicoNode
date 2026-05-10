namespace PicoNode.Tests;

public sealed class UdpNodeDatagramFlowTests
{
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

            await Assert.That(logger.Calls.Any(x => x.EventId.Id == (int)NodeFaultCode.DatagramDropped)).IsTrue();
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
        var context = new UdpDatagramContext(node, new IPEndPoint(IPAddress.Loopback, 45678));

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

        await Assert.That(logger.Calls.Any(x => x.EventId.Id == (int)NodeFaultCode.DatagramReceiveFailed)).IsTrue();
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

        public async Task OnDatagramAsync(
            IUdpDatagramContext context,
            ArraySegment<byte> datagram,
            CancellationToken cancellationToken
        )
        {
            var copy = datagram.ToArray();
            Received.TrySetResult(new ReceivedUdpDatagram(context.RemoteEndPoint, copy));

            if (_reply.Length > 0)
            {
                await context.SendAsync(new ArraySegment<byte>(_reply), cancellationToken);
            }
        }
    }

    private sealed class ThrowingUdpHandler : IUdpDatagramHandler
    {
        public TaskCompletionSource<Exception> ExceptionObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnDatagramAsync(
            IUdpDatagramContext context,
            ArraySegment<byte> datagram,
            CancellationToken cancellationToken
        )
        {
            var exception = new InvalidOperationException("handler boom");
            ExceptionObserved.TrySetResult(exception);
            return Task.FromException(exception);
        }
    }

    private sealed class BlockingUdpHandler : IUdpDatagramHandler
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstInvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnDatagramAsync(
            IUdpDatagramContext context,
            ArraySegment<byte> datagram,
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

    private sealed record ReceivedUdpDatagram(IPEndPoint RemoteEndPoint, byte[] Datagram);
}
