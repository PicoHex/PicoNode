namespace PicoWeb.Integration.Tests;

public sealed class Http2Tests
{
    private static X509Certificate2? LoadDevCert()
    {
        try
        {
            using var store = new X509Store("My", StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var certs = store.Certificates.Find(
                X509FindType.FindBySubjectName,
                "localhost",
                validOnly: false
            );
            foreach (var c in certs)
            {
                if (c.FriendlyName?.Contains("ASP.NET", StringComparison.OrdinalIgnoreCase) == true)
                    return c;
            }
        }
        catch { /* intentional */ }
        return null;
    }

    [Test]
    public async Task Http1_works_as_baseline()
    {
        var port = GetRandomPort();
        var app = new WebApp(new DummyContainer());
        app.MapGet(
            "/api/health",
            static (WebContext ctx, CancellationToken _) =>
                ValueTask.FromResult(WebResults.Json(200, """{"ok":true}""", "OK"))
        );

        await using var server = new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
        await server.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).Contains("ok");
    }

    [Test]
    public async Task Http2_h2c_upgrade_works()
    {
        var port = GetRandomPort();
        var app = new WebApp(new DummyContainer());
        app.MapGet(
            "/api/health",
            static (WebContext ctx, CancellationToken _) =>
                ValueTask.FromResult(WebResults.Json(200, """{"ok":true}""", "OK"))
        );

        await using var server = new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
        await server.StartAsync();

        using var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Version).IsEqualTo(System.Net.HttpVersion.Version20);
        await Assert.That(body).Contains("ok");
    }

    [Test]
    public async Task Http2_tls_alpn_works()
    {
        var cert = LoadDevCert();
        if (cert is null)
            return;

        var port = GetRandomPort();
        var app = new WebApp(new DummyContainer());
        app.MapGet(
            "/api/health",
            static (WebContext ctx, CancellationToken _) =>
                ValueTask.FromResult(WebResults.Json(200, """{"ok":true}""", "OK"))
        );

        await using var server = new WebServer(
            app,
            new WebServerOptions
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, port),
                SslOptions = new()
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols =
                    [
                        SslApplicationProtocol.Http2,
                        SslApplicationProtocol.Http11,
                    ],
                },
            }
        );
        await server.StartAsync();

        using var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                ApplicationProtocols = [SslApplicationProtocol.Http2],
            },
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://localhost:{port}"),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Version).IsEqualTo(System.Net.HttpVersion.Version20);
        await Assert.That(body).Contains("ok");
    }

    private static int GetRandomPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class DummyContainer : PicoDI.Abs.ISvcContainer
    {
        public PicoDI.Abs.ISvcContainer Register(PicoDI.Abs.SvcDescriptor descriptor) => this;

        public bool IsRegistered(Type serviceType) => false;

        public PicoDI.Abs.ISvcScope CreateScope() => new DummyScope();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyScope : PicoDI.Abs.ISvcScope
    {
        public object GetService(Type serviceType) => null!;

        public IReadOnlyList<object> GetServices(Type serviceType) => [];

        public bool TryGetService(Type serviceType, out object? result)
        {
            result = null;
            return false;
        }

        public bool TryGetServices(Type serviceType, out IReadOnlyList<object>? result)
        {
            result = null;
            return false;
        }

        public PicoDI.Abs.ISvcScope CreateScope() => this;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
