namespace PicoNode.Tests;

public sealed class TcpConnectionFaultTests
{
    [Test]
    public async Task CloseCoreAsync_tolerates_a_connection_token_its_dispose_already_released()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handler = new RecordingCloseHandler();
            var node = new TcpNode(
                new TcpNodeOptions
                {
                    Endpoint = (IPEndPoint)pair.Server.LocalEndPoint!,
                    ConnectionHandler = handler,
                    Logger = logger,
                }
            );
            var connection = new TcpConnection(node, pair.Server);

            // The shape of a close racing a dispose: the connection token's source is already
            // disposed by the time the close path fires it. Nobody is left to wait on that token,
            // so it is not a fault to report — but it must not abandon the steps that follow it
            // either (the socket shutdown, the wait for the handler loop, OnClosedAsync, the
            // pipe/socket teardown), which is what reporting-and-skipping them used to buy.
            var releasedCts = new CancellationTokenSource();
            releasedCts.Dispose();
            var lifecycle = typeof(TcpConnection)
                .GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(connection)!;
            typeof(TcpConnectionLifecycle)
                .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(lifecycle, releasedCts);

            connection.Close();

            var closed = await handler.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert
                .That(closed)
                .IsEqualTo(TcpCloseReason.LocalClose)
                .Because("the close sequence still ran to its end");
            await Assert
                .That(logger.Calls.Count)
                .IsEqualTo(0)
                .Because("a source a concurrent dispose already released is not a fault");
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
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

    [Test]
    public async Task RunAsync_closes_with_handler_fault_when_OnConnectedAsync_throws()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var handlerException = new InvalidOperationException("connected boom");
            var handler = new ThrowingOnConnectedHandler(handlerException);
            var node = new TcpNode(
                new TcpNodeOptions
                {
                    Endpoint = (IPEndPoint)pair.Server.LocalEndPoint!,
                    ConnectionHandler = handler,
                    Logger = logger,
                }
            );
            var connection = new TcpConnection(node, pair.Server);
            var runTask = connection.RunAsync(handler);

            await Task.Delay(500);
            var closed = await handler.CloseReason.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await runTask.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(closed).IsEqualTo(TcpCloseReason.HandlerFailed);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    private sealed class RecordingCloseHandler : ITcpConnectionHandler
    {
        public TaskCompletionSource<TcpCloseReason> Closed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        )
        {
            Closed.TrySetResult(reason);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingOnConnectedHandler(Exception exception) : ITcpConnectionHandler
    {
        public TaskCompletionSource<TcpCloseReason> CloseReason { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnConnectedAsync(
            ITcpConnectionContext connection,
            CancellationToken cancellationToken
        ) => throw exception;

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
            CloseReason.TrySetResult(reason);
            return ValueTask.CompletedTask;
        }
    }
}
