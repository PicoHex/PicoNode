namespace PicoNode.Tests;

public sealed class TcpConnectionFaultTests
{
    [Test]
    public async Task CloseCoreAsync_reports_fault_when_internal_step_throws()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var logger = new SpyLogger();
            var faultReported = new TaskCompletionSource<LogCall>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            var connection = CreateConnection(pair.Server, logger);

            var disposedCts = new CancellationTokenSource();
            disposedCts.Dispose();
            var lifecycleField = typeof(TcpConnection).GetField(
                "_lifecycle",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            var lifecycle = lifecycleField.GetValue(connection)!;
            var ctsField = typeof(TcpConnectionLifecycle).GetField(
                "_cts",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            ctsField.SetValue(lifecycle, disposedCts);

            connection.Close();

            await Task.Delay(500);

            await Assert.That(logger.Calls.Count).IsEqualTo(1);
            await Assert.That(logger.Calls.TryPeek(out var call)).IsTrue();
            await Assert.That(call!.Level).IsEqualTo(LogLevel.Error);
            await Assert.That(call.EventId.Id).IsEqualTo((int)NodeFaultCode.HandlerFailed);
            await Assert.That(call.Exception).IsTypeOf<ObjectDisposedException>();
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    private static TcpConnection CreateConnection(Socket serverSocket, ILogger? logger = null)
    {
        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = (IPEndPoint)serverSocket.LocalEndPoint!,
                ConnectionHandler = new NoOpTcpHandler(),
                Logger = logger,
            }
        );

        return new TcpConnection(node, serverSocket);
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
