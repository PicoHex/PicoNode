namespace PicoNode.Tests;

public sealed class TcpNodeTlsAcceptLoopTests
{
    [Test]
    public async Task TrackAndRunAsync_accepts_optional_stream_parameter()
    {
        var method = typeof(TcpNode).GetMethod(
            "TrackAndRunAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));
        await Assert.That(method.GetParameters().Length).IsEqualTo(2);
        await Assert.That(method.GetParameters()[0].ParameterType).IsEqualTo(typeof(Socket));
        await Assert.That(method.GetParameters()[1].ParameterType).IsEqualTo(typeof(Stream));
        await Assert.That(method.GetParameters()[1].HasDefaultValue).IsTrue();
    }

    [Test]
    public async Task MaxConnections_check_rejects_before_tls_when_ssl_configured()
    {
        var faults = new ConcurrentQueue<NodeFault>();
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateTlsNode(faults.Enqueue, maxConnections: 1);

            // Pre-fill the connections dictionary to trigger the limit check
            var dummySocket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp
            );
            var dummyConnection = new TcpConnection(node, dummySocket, stream: null);
            GetConnections(node).TryAdd(dummyConnection.Id, dummyConnection);

            await InvokeProcessAcceptedSocketAsync(node, pair.Server);

            await Assert.That(faults.Count).IsEqualTo(1);
            await Assert.That(faults.TryPeek(out var fault)).IsTrue();
            await Assert.That(fault!.Code).IsEqualTo(NodeFaultCode.SessionRejected);
            await Assert.That(fault.Operation).IsEqualTo("tcp.reject.limit");
            await Assert
                .That(() => pair.Server.Send(new byte[] { 1 }, SocketFlags.None))
                .Throws<ObjectDisposedException>();

            await dummyConnection.DisposeAsync();
            dummySocket.Dispose();
            await node.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
        }
    }

    [Test]
    public async Task ProcessAcceptedSocketAsync_tls_path_returns_immediately_without_awaiting_handshake()
    {
        // Verify the structural change: when SslOptions is set,
        // ProcessAcceptedSocketAsync should return a completed task
        // (fire-and-forget), not an in-progress task that represents the TLS handshake.
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            using var cert = CreateSelfSignedCertificate();
            var node = CreateTlsNode(maxConnections: 10, cert: cert);

            var method = typeof(TcpNode).GetMethod(
                "ProcessAcceptedSocketAsync",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;

            var task = (Task)method.Invoke(node, [pair.Server])!;

            // The task should complete quickly since TLS is fire-and-forget.
            // It should not be awaiting the TLS handshake.
            var completed = await CompletesWithinAsync(task, TimeSpan.FromMilliseconds(500));
            await Assert.That(completed).IsTrue();

            await node.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
        }
    }

    [Test]
    public async Task Non_tls_path_still_tracks_and_increments_accepted()
    {
        var faults = new ConcurrentQueue<NodeFault>();
        var pair = await CreateConnectedSocketsAsync();
        try
        {
            var node = CreateNode(faults.Enqueue);

            var connectionsBefore = GetConnections(node).Count;
            await InvokeProcessAcceptedSocketAsync(node, pair.Server);

            await Assert.That(GetConnections(node).Count).IsEqualTo(connectionsBefore + 1);
            await Assert.That(faults.Count).IsEqualTo(0);

            await node.DisposeAsync();
        }
        finally
        {
            pair.Client.Dispose();
        }
    }

    private static TcpNode CreateTlsNode(
        Action<NodeFault>? faultHandler = null,
        int maxConnections = 10,
        X509Certificate2? cert = null
    )
    {
        var certificate = cert ?? CreateSelfSignedCertificate();
        return new TcpNode(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = new NoOpTcpHandler(),
                FaultHandler = faultHandler,
                MaxConnections = maxConnections,
                SslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                },
            }
        );
    }

    private static TcpNode CreateNode(Action<NodeFault> faultHandler) =>
        new(
            new TcpNodeOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 0),
                ConnectionHandler = new NoOpTcpHandler(),
                FaultHandler = faultHandler,
            }
        );

    private static async Task InvokeProcessAcceptedSocketAsync(TcpNode node, Socket socket)
    {
        var method = typeof(TcpNode).GetMethod(
            "ProcessAcceptedSocketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        await (Task)method.Invoke(node, [socket])!;
    }

    private static ConcurrentDictionary<long, TcpConnection> GetConnections(TcpNode node)
    {
        var field = typeof(TcpNode).GetField(
            "_connections",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        return (ConcurrentDictionary<long, TcpConnection>)field.GetValue(node)!;
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

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1)
        );
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
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
}


