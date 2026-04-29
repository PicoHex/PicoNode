namespace PicoNode.Tests;

public sealed class TcpConnectionFaultTests
{
    [Test]
    public async Task CloseCoreAsync_reports_fault_when_internal_step_throws()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var faults = new ConcurrentQueue<NodeFault>();
            var faultReported = new TaskCompletionSource<NodeFault>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            var connection = CreateConnection(
                pair.Server,
                fault =>
                {
                    faults.Enqueue(fault);
                    if (fault.Operation == "tcp.close.core")
                    {
                        faultReported.TrySetResult(fault);
                    }
                }
            );

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

            var fault = await faultReported.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await Assert.That(faults.Count).IsEqualTo(1);
            await Assert.That(fault.Code).IsEqualTo(NodeFaultCode.HandlerFailed);
            await Assert.That(fault.Operation).IsEqualTo("tcp.close.core");
            await Assert.That(fault.Exception).IsTypeOf<ObjectDisposedException>();
            await connection.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    private static TcpConnection CreateConnection(
        Socket serverSocket,
        Action<NodeFault>? faultHandler = null
    )
    {
        var node = new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = (IPEndPoint)serverSocket.LocalEndPoint!,
                ConnectionHandler = new NoOpTcpHandler(),
                FaultHandler = faultHandler,
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

        public Task OnClosedAsync(
            ITcpConnectionContext connection,
            TcpCloseReason reason,
            Exception? error,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
