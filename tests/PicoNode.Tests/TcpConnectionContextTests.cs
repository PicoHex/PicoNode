namespace PicoNode.Tests;

public sealed class TcpConnectionContextTests
{
    [Test]
    public async Task Context_exposes_underlying_connection_metadata()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode((IPEndPoint)pair.Server.LocalEndPoint!);
            var connection = new TcpConnection(node, pair.Server);
            var context = new TcpConnectionContext(connection);

            await Assert.That(context.ConnectionId).IsEqualTo(connection.Id);
            await Assert.That(context.RemoteEndPoint).IsEqualTo(connection.RemoteEndPoint);
            await Assert.That(context.ConnectedAtUtc).IsEqualTo(connection.ConnectedAtUtc);
            await Assert.That(context.LastActivityUtc).IsEqualTo(connection.LastActivityUtc);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task SendAsync_delegates_to_connection()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode((IPEndPoint)pair.Server.LocalEndPoint!);
            var connection = new TcpConnection(node, pair.Server);
            var context = new TcpConnectionContext(connection);

            var payload = new byte[] { 1, 2, 3, 4 };
            await context.SendAsync(new ReadOnlySequence<byte>(payload));

            var buffer = new byte[payload.Length];
            var read = await pair.Client.ReceiveAsync(buffer, SocketFlags.None);

            await Assert.That(read).IsEqualTo(payload.Length);
            await Assert.That(buffer).IsEquivalentTo(payload);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task Close_delegates_to_connection()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode((IPEndPoint)pair.Server.LocalEndPoint!);
            var connection = new TcpConnection(node, pair.Server);
            var context = new TcpConnectionContext(connection);

            context.Close();
            await Task.Delay(100);

            await Assert
                .That(() => context.SendAsync(new ReadOnlySequence<byte>(new byte[] { 7 })))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task IsIdle_returns_false_for_non_positive_timeout()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode((IPEndPoint)pair.Server.LocalEndPoint!);
            var connection = new TcpConnection(node, pair.Server);

            await Assert.That(connection.IsIdle(TimeSpan.Zero)).IsFalse();
            await Assert.That(connection.IsIdle(TimeSpan.FromMilliseconds(-1))).IsFalse();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task IsIdle_returns_true_when_last_activity_exceeds_timeout()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode((IPEndPoint)pair.Server.LocalEndPoint!);
            var connection = new TcpConnection(node, pair.Server);

            typeof(TcpConnection)
                .GetField("_lastActivityTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(connection, DateTimeOffset.UtcNow.AddMinutes(-5).UtcTicks);

            await Assert.That(connection.IsIdle(TimeSpan.FromSeconds(1))).IsTrue();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    private static TcpNode CreateNode(IPEndPoint endpoint) =>
        new(new TcpNodeOptions { Endpoint = endpoint, ConnectionHandler = new NoOpTcpHandler(), });

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
}
