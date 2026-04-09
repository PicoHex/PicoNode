using System.Reflection;

namespace PicoNode.Tests;

public sealed class TcpNodeMetricsTests
{
    [Test]
    public async Task GetMetrics_returns_zero_counters_before_any_activity()
    {
        await using var node = CreateNode(new EchoTcpHandler());
        await node.StartAsync();

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalAccepted).IsEqualTo(0);
        await Assert.That(metrics.TotalRejected).IsEqualTo(0);
        await Assert.That(metrics.TotalClosed).IsEqualTo(0);
        await Assert.That(metrics.ActiveConnections).IsEqualTo(0);
        await Assert.That(metrics.TotalBytesSent).IsEqualTo(0);
        await Assert.That(metrics.TotalBytesReceived).IsEqualTo(0);
    }

    [Test]
    public async Task GetMetrics_tracks_accepted_and_active_connections()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler);
        await node.StartAsync();

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);

        await handler.WaitConnectedAsync();

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalAccepted).IsEqualTo(1);
        await Assert.That(metrics.ActiveConnections).IsEqualTo(1);
    }

    [Test]
    public async Task GetMetrics_tracks_closed_connections()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler);
        await node.StartAsync();

        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        client.Shutdown(SocketShutdown.Both);
        client.Dispose();

        await handler.WaitClosedAsync();

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalAccepted).IsEqualTo(1);
        await Assert.That(metrics.TotalClosed).IsEqualTo(1);
        await Assert.That(metrics.ActiveConnections).IsEqualTo(0);
    }

    [Test]
    public async Task GetMetrics_tracks_bytes_received()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler);
        await node.StartAsync();

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        var data = "Hello, World!"u8.ToArray();
        await client.SendAsync(data, SocketFlags.None);

        await handler.WaitDataReceivedAsync();

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalBytesReceived).IsGreaterThanOrEqualTo(data.Length);
    }

    [Test]
    public async Task GetMetrics_tracks_bytes_sent()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler);
        await node.StartAsync();

        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        var data = "Hello!"u8.ToArray();
        await client.SendAsync(data, SocketFlags.None);

        var buffer = new byte[256];
        var received = await client.ReceiveAsync(buffer, SocketFlags.None);

        await Assert.That(received).IsEqualTo(data.Length);

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalBytesSent).IsGreaterThanOrEqualTo(data.Length);
    }

    [Test]
    public async Task GetMetrics_tracks_rejected_connections_at_max()
    {
        var handler = new EchoTcpHandler();
        await using var node = CreateNode(handler, maxConnections: 1);
        await node.StartAsync();

        using var client1 = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client1.ConnectAsync((IPEndPoint)node.LocalEndPoint);
        await handler.WaitConnectedAsync();

        using var client2 = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client2.ConnectAsync((IPEndPoint)node.LocalEndPoint);

        // Wait briefly for the rejection to process
        await Task.Delay(200);

        var metrics = node.GetMetrics();

        await Assert.That(metrics.TotalRejected).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task TcpNodeMetrics_exposes_expected_properties()
    {
        var type = typeof(TcpNodeMetrics);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert
            .That(properties)
            .IsEquivalentTo(

                [
                    nameof(TcpNodeMetrics.ActiveConnections),
                    nameof(TcpNodeMetrics.TotalAccepted),
                    nameof(TcpNodeMetrics.TotalBytesSent),
                    nameof(TcpNodeMetrics.TotalBytesReceived),
                    nameof(TcpNodeMetrics.TotalClosed),
                    nameof(TcpNodeMetrics.TotalRejected),
                ]
            );
    }

    [Test]
    public async Task UdpNodeMetrics_exposes_expected_properties()
    {
        var type = typeof(UdpNodeMetrics);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToArray();

        await Assert.That(type.IsSealed).IsTrue();
        await Assert
            .That(properties)
            .IsEquivalentTo(

                [
                    nameof(UdpNodeMetrics.TotalBytesReceived),
                    nameof(UdpNodeMetrics.TotalBytesSent),
                    nameof(UdpNodeMetrics.TotalDatagramsReceived),
                    nameof(UdpNodeMetrics.TotalDatagramsSent),
                    nameof(UdpNodeMetrics.TotalDropped),
                ]
            );
    }

    private static TcpNode CreateNode(ITcpConnectionHandler handler, int maxConnections = 100) =>
        new(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = handler,
                MaxConnections = maxConnections,
            }
        );

    private sealed class EchoTcpHandler : ITcpConnectionHandler
    {
        private readonly TaskCompletionSource _connected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _dataReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitConnectedAsync() => _connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public Task WaitClosedAsync() => _closed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public Task WaitDataReceivedAsync() =>
            _dataReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        )
        {
            _connected.TrySetResult();
            return Task.CompletedTask;
        }

        public async ValueTask<SequencePosition> OnReceivedAsync(
            ITcpConnectionContext connection,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken
        )
        {
            _dataReceived.TrySetResult();
            await connection.SendAsync(buffer, cancellationToken);
            return buffer.End;
        }

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        )
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
