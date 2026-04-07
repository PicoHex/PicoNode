namespace PicoNode.Tests;

public sealed class SocketIoEventArgsTests
{
    [Test]
    public async Task Reset_clears_mutable_state()
    {
        using var eventArgs = new SocketIoEventArgs
        {
            AcceptSocket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp
            ),
            DisconnectReuseSocket = true,
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
            UserToken = new object(),
        };

        eventArgs.SetBuffer(new byte[32], 0, 32);
        eventArgs.Reset();

        await Assert.That(eventArgs.AcceptSocket).IsNull();
        await Assert.That(eventArgs.DisconnectReuseSocket).IsFalse();
        await Assert.That(eventArgs.RemoteEndPoint).IsNull();
        await Assert.That(eventArgs.UserToken).IsNull();
        await Assert.That(eventArgs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AcceptAsync_returns_completed_value_task_when_socket_completes_synchronously()
    {
        using var listener = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var endpoint = (IPEndPoint)listener.LocalEndPoint!;
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        await client.ConnectAsync(endpoint);

        using var eventArgs = new SocketIoEventArgs();
        var acceptTask = eventArgs.AcceptAsync(listener);
        var completedArgs = await acceptTask;

        await Assert.That(ReferenceEquals(completedArgs, eventArgs)).IsTrue();
        await Assert.That(eventArgs.AcceptSocket).IsNotNull();

        eventArgs.AcceptSocket?.Dispose();
    }

    [Test]
    public async Task ReceiveAsync_reads_available_data()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            await pair.Client.SendAsync(payload, SocketFlags.None);

            using var eventArgs = new SocketIoEventArgs();
            eventArgs.SetBuffer(new byte[16], 0, 16);

            var completedArgs = await eventArgs.ReceiveAsync(pair.Server);

            await Assert.That(ReferenceEquals(completedArgs, eventArgs)).IsTrue();
            await Assert.That(eventArgs.BytesTransferred).IsEqualTo(payload.Length);
            await Assert
                .That(eventArgs.Buffer!.Take(payload.Length).ToArray())
                .IsEquivalentTo(payload);
        }
        finally
        {
            pair.Client.Dispose();
            pair.Server.Dispose();
        }
    }

    [Test]
    public async Task SendAsync_writes_buffer_to_peer_socket()
    {
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var payload = new byte[] { 5, 6, 7, 8 };

            using var eventArgs = new SocketIoEventArgs();
            eventArgs.SetBuffer(payload, 0, payload.Length);

            var completedArgs = await eventArgs.SendAsync(pair.Server);

            var buffer = new byte[payload.Length];
            var read = await pair.Client.ReceiveAsync(buffer, SocketFlags.None);

            await Assert.That(ReferenceEquals(completedArgs, eventArgs)).IsTrue();
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
    public async Task ExecuteAsync_throws_when_operation_is_already_pending()
    {
        using var eventArgs = new SocketIoEventArgs();
        var startSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        _ = InvokeExecuteAsync(
            eventArgs,
            _ =>
            {
                startSignal.TrySetResult();
                return true;
            }
        );

        await startSignal.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var exception = await Assert
            .That(async () => await InvokeExecuteAsync(eventArgs, _ => false))
            .Throws<TargetInvocationException>();
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.InnerException).IsAssignableTo<InvalidOperationException>();

        CompletePendingOperation(eventArgs);
    }

    [Test]
    public async Task ExecuteAsync_resets_pending_state_when_start_throws()
    {
        using var eventArgs = new SocketIoEventArgs();

        var exception = await Assert
            .That(
                async () =>
                    await InvokeExecuteAsync(
                        eventArgs,
                        _ => throw new SocketException((int)SocketError.NotConnected)
                    )
            )
            .Throws<TargetInvocationException>();
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.InnerException).IsAssignableTo<SocketException>();

        var retry = InvokeExecuteAsync(eventArgs, _ => false);
        await Assert.That(retry.IsCompletedSuccessfully).IsTrue();
        await Assert.That(ReferenceEquals(await retry, eventArgs)).IsTrue();
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

    private static ValueTask<SocketAsyncEventArgs> InvokeExecuteAsync(
        SocketIoEventArgs eventArgs,
        Func<SocketAsyncEventArgs, bool> start
    )
    {
        var method = typeof(SocketIoEventArgs).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        return (ValueTask<SocketAsyncEventArgs>)method.Invoke(eventArgs, [start])!;
    }

    private static void CompletePendingOperation(SocketIoEventArgs eventArgs)
    {
        var method = typeof(SocketIoEventArgs).GetMethod(
            "OnCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(object), typeof(SocketAsyncEventArgs)]
        )!;

        method.Invoke(eventArgs, [eventArgs, eventArgs]);
    }
}
