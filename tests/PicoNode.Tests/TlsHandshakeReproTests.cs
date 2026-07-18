namespace PicoNode.Tests;

/// <summary>
/// Isolated TDD tests to reproduce the TLS handshake EOF issue observed
/// in SmokeTests on .NET 10. These tests bypass TcpNode entirely to
/// determine whether the issue is in the runtime or in PicoNode's TLS code.
/// </summary>
public sealed class TlsHandshakeReproTests
{
    private static X509Certificate2 CreateSelfSignedCert()
    {
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: true
            )
        );
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1)
        );
        // .NET 10: PFX roundtrip avoids "platform does not support ephemeral keys"
        var pfx = cert.Export(X509ContentType.Pfx, "temp");
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            "temp",
            X509KeyStorageFlags.Exportable,
            loaderLimits: null!
        );
    }

    /// <summary>
    /// RED: Minimal .NET SslStream test — does a raw SslStream
    /// handshake succeed on .NET 10 with a self-signed cert?
    /// </summary>
    [Test]
    public async Task RawSslStream_handshake_succeeds_on_dotnet10()
    {
        using var cert = CreateSelfSignedCert();
        var port = GetAvailablePort();

        // Server: simple TCP listener
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        // Start server handshake in background
        var serverTask = Task.Run(async () =>
        {
            using var serverSocket = await listener.AcceptSocketAsync();
            using var networkStream = new NetworkStream(serverSocket, ownsSocket: false);
            using var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = cert }
            );
            // success — read one byte to confirm stream is alive
            var buf = new byte[1];
            await sslStream.ReadExactlyAsync(buf);
            return buf[0];
        });

        // Client: connect and perform TLS handshake
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port);
        using var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            static (_, _, _, _) => true
        );
        await sslStream.AuthenticateAsClientAsync("localhost");
        sslStream.WriteByte(42);
        await sslStream.FlushAsync();

        var received = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(received).IsEqualTo((byte)42);

        listener.Stop();
    }

    /// <summary>
    /// RED: Fire-and-forget Task.Run pattern (matching TcpNode's code)
    /// — does the TLS handshake work when offloaded?
    /// </summary>
    [Test]
    public async Task TlsHandshake_via_TaskRun_offload_succeeds()
    {
        using var cert = CreateSelfSignedCert();
        var port = GetAvailablePort();

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var tcs = new TaskCompletionSource<SslStream>();
        var serverError = (Exception?)null;

        // Simulate TcpNode's fire-and-forget TLS offload pattern:
        // Accept → Task.Run → negotiate TLS → signal ready
        _ = Task.Run(async () =>
        {
            try
            {
                using var socket = await listener.AcceptSocketAsync();
                var networkStream = new NetworkStream(socket, ownsSocket: false);
                var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions { ServerCertificate = cert }
                );
                tcs.SetResult(sslStream);
            }
            catch (Exception ex)
            {
                serverError = ex;
                tcs.TrySetException(ex);
            }
        });

        // Client: same as before
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port);
        using var clientSsl = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            static (_, _, _, _) => true
        );
        await clientSsl.AuthenticateAsClientAsync("localhost");

        var serverSsl = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(serverError).IsNull();
        await Assert.That(serverSsl.IsAuthenticated).IsTrue();
        await Assert.That(serverSsl.IsEncrypted).IsTrue();

        await serverSsl.DisposeAsync();
        listener.Stop();
    }

    private static int GetAvailablePort()
    {
        using var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

file sealed class TcpEchoHandler : ITcpConnectionHandler
{
    public Task OnConnectedAsync(
        ITcpConnectionContext connection,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    ) => new(buffer.End);

    public ValueTask OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    ) => default;

    public ValueTask OnFaultedAsync(
        ITcpConnectionContext connection,
        Exception exception,
        CancellationToken cancellationToken
    ) => default;
}
