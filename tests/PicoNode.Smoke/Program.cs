using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PicoNode;
using PicoNode.Abs;

public sealed class SmokeTests
{
    [Test]
    public async Task RunTcpSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = new TcpEchoHandler(),
                DrainTimeout = TimeSpan.FromSeconds(2),
            }
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var payload = new byte[] { 1, 2, 3, 4 };
        await stream.WriteAsync(payload);

        var buffer = new byte[payload.Length];
        await ReadExactAsync(stream, buffer);
        await AssertPayloadEqualAsync(buffer, payload);
    }

    [Test]
    public async Task RunUdpSmokeAsync()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = new UdpEchoHandler(),
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        var payload = new byte[] { 9, 8, 7, 6 };
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
        var result = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await AssertPayloadEqualAsync(result.Buffer, payload);
    }

    [Test]
    public async Task StopAsync_waits_for_connection_close_completion()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        var handler = new BlockingCloseHandler();

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = handler,
                DrainTimeout = TimeSpan.FromSeconds(5),
            }
        );

        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await handler.Connected.WaitAsync(TimeSpan.FromSeconds(5));

        Task? stopTask = null;
        try
        {
            stopTask = node.StopAsync();
            await handler.CloseStarted.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert
                .That(await CompletesWithinAsync(stopTask, TimeSpan.FromMilliseconds(200)))
                .IsFalse();
        }
        finally
        {
            handler.AllowClose();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
                await handler.CloseCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task StopAsync_rejects_new_connections_while_stopping()
    {
        var port = GetAvailablePort(SocketType.Stream, ProtocolType.Tcp);
        var handler = new BlockingCloseHandler();

        await using var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                ConnectionHandler = handler,
                DrainTimeout = TimeSpan.FromSeconds(5),
            }
        );

        await node.StartAsync();

        using var firstClient = new TcpClient();
        await firstClient.ConnectAsync(IPAddress.Loopback, port);
        await handler.Connected.WaitAsync(TimeSpan.FromSeconds(5));

        Task? stopTask = null;
        try
        {
            stopTask = node.StopAsync();
            await handler.CloseStarted.WaitAsync(TimeSpan.FromSeconds(5));

            var secondConnectionAccepted = await AttemptLateTcpConnectionAsync(port);
            await Assert.That(secondConnectionAccepted).IsFalse();
            await Assert.That(handler.ConnectionCount).IsEqualTo(1);
        }
        finally
        {
            handler.AllowClose();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
                await handler.CloseCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task StopAsync_waits_for_inflight_udp_handler_completion()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);
        var handler = new BlockingUdpHandler();

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = handler,
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        await client.SendAsync(new byte[] { 1, 2, 3 }, 3, new IPEndPoint(IPAddress.Loopback, port));
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));

        Task? stopTask = null;
        try
        {
            stopTask = node.StopAsync();
            await Assert
                .That(await CompletesWithinAsync(stopTask, TimeSpan.FromMilliseconds(200)))
                .IsFalse();
        }
        finally
        {
            handler.Release();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
                await handler.Completed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task Udp_handler_fault_reports_fault_and_subsequent_datagram_still_processes()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);
        var faults = new ConcurrentQueue<NodeFaultCode>();

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = new ThrowOnceThenEchoUdpHandler(),
                FaultHandler = fault => faults.Enqueue(fault.Code),
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        var remote = new IPEndPoint(IPAddress.Loopback, port);

        await client.SendAsync(new byte[] { 0x10 }, 1, remote);
        await Task.Delay(200);

        await client.SendAsync(new byte[] { 0x20, 0x21 }, 2, remote);
        var echoed = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(faults.Contains(NodeFaultCode.DatagramHandlerFailed)).IsTrue();
        await AssertPayloadEqualAsync(echoed.Buffer, new byte[] { 0x20, 0x21 });
    }

    [Test]
    public async Task Udp_drop_newest_reports_fault_when_dispatch_queue_is_saturated()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);
        var handler = new BlockingUdpHandler();
        var faults = new ConcurrentQueue<NodeFaultCode>();

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = handler,
                DispatchWorkerCount = 1,
                DatagramQueueCapacity = 1,
                QueueOverflowMode = UdpOverflowMode.DropNewest,
                FaultHandler = fault => faults.Enqueue(fault.Code),
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        var remote = new IPEndPoint(IPAddress.Loopback, port);

        await client.SendAsync(new byte[] { 1 }, 1, remote);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 32; i++)
        {
            await client.SendAsync(new byte[] { (byte)(i + 2) }, 1, remote);
        }

        await EventuallyAsync(
            () => Task.FromResult(faults.Contains(NodeFaultCode.DatagramDropped)),
            TimeSpan.FromSeconds(5)
        );

        handler.Release();
        await handler.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(faults.Contains(NodeFaultCode.DatagramDropped)).IsTrue();
    }

    [Test]
    public async Task Udp_stop_cancellation_leaves_state_as_stopping_until_drain_finishes()
    {
        var port = GetAvailablePort(SocketType.Dgram, ProtocolType.Udp);
        var handler = new BlockingUdpHandler();

        await using var node = new UdpNode(
            new UdpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                DatagramHandler = handler,
            }
        );

        await node.StartAsync();

        using var client = new UdpClient();
        await client.SendAsync(new byte[] { 0x33 }, 1, new IPEndPoint(IPAddress.Loopback, port));
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.That(async () => await node.StopAsync(stopCts.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(node.State).IsEqualTo(NodeState.Stopping);

        handler.Release();
        await node.StopAsync();
        await handler.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(node.State).IsEqualTo(NodeState.Stopped);
    }

    private static int GetAvailablePort(SocketType socketType, ProtocolType protocolType)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, socketType, protocolType);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0)
            {
                throw new InvalidOperationException("Connection closed while reading.");
            }

            offset += read;
        }
    }

    private static async Task AssertPayloadEqualAsync(byte[] actual, byte[] expected)
    {
        await Assert.That(actual.Length).IsEqualTo(expected.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
        }
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private static async Task EventuallyAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null
    )
    {
        var deadline = DateTime.UtcNow + timeout;
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(25);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(interval);
        }

        throw new TimeoutException("Condition was not met before the timeout elapsed.");
    }

    private static async Task<bool> AttemptLateTcpConnectionAsync(int port)
    {
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (SocketException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }

        try
        {
            using var stream = client.GetStream();
            await stream
                .WriteAsync(new byte[] { 0x42 }.AsMemory())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));

            var buffer = new byte[1];
            var read = await stream
                .ReadAsync(buffer.AsMemory())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));
            return read > 0;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

file sealed class TcpEchoHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        // Echo: 将接收到的数据原样发送回去，并消费整个缓冲区
        _ = connection.SendAsync(buffer, cancellationToken);
        return ValueTask.FromResult(buffer.End);
    }
}

file sealed class BlockingCloseHandler : ITcpConnectionHandler
{
    private int _connectionCount;
    private readonly TaskCompletionSource _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowClose =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Connected => _connected.Task;

    public Task CloseStarted => _closeStarted.Task;

    public Task CloseCompleted => _closeCompleted.Task;

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _connectionCount);
        _connected.TrySetResult();
        return Task.CompletedTask;
    }

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(buffer.End);

    public async Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    )
    {
        _closeStarted.TrySetResult();

        try
        {
            await _allowClose.Task;
        }
        finally
        {
            _closeCompleted.TrySetResult();
        }
    }

    public void AllowClose() => _allowClose.TrySetResult();
}

file sealed class UdpEchoHandler : IUdpDatagramHandler
{
    public Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    ) => context.SendAsync(datagram, cancellationToken);
}

file sealed class BlockingUdpHandler : IUdpDatagramHandler
{
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public Task Completed => _completed.Task;

    public async Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    )
    {
        _started.TrySetResult();

        try
        {
            await _release.Task.WaitAsync(cancellationToken);
            await context.SendAsync(datagram, cancellationToken);
        }
        finally
        {
            _completed.TrySetResult();
        }
    }

    public void Release() => _release.TrySetResult();
}

file sealed class ThrowOnceThenEchoUdpHandler : IUdpDatagramHandler
{
    private int _failed;

    public Task OnDatagramAsync(
        IUdpDatagramContext context,
        ArraySegment<byte> datagram,
        CancellationToken cancellationToken
    )
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
        {
            throw new InvalidOperationException("boom");
        }

        return context.SendAsync(datagram, cancellationToken);
    }
}
