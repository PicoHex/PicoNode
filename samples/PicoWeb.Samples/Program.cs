using PicoLog;
using PicoLog.Abs;

var container = new SvcContainer();
container.RegisterScoped<PicoWeb.Samples.Controllers.UsersController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.ProductsController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.PostsController>();
container.Build();

// WebSocket echo handler
WebSocketMessageHandler wsEcho = static async (msg, conn, ct) =>
{
    if (msg.OpCode == WebSocketOpCode.Close)
        return;
    var size = WebSocketFrameCodec.MeasureFrameSize(msg.Payload.Length, mask: false);
    var buf = new byte[size];
    WebSocketFrameCodec.WriteFrame(buf, msg.OpCode, msg.Payload.Span);
    await conn.SendAsync(new ReadOnlySequence<byte>(buf), ct);
};

var app = PicoWeb.Samples.ShowcaseApp.Create(container, webSocketHandler: wsEcho);
EndpointRegistrar.RegisterAll(app);

// Use --http to force plain HTTP, --fresh-cert to create a new runtime cert
var useHttp = args.Contains("--http");
var useFreshCert = args.Contains("--fresh-cert");

// Try to load ASP.NET Core dev cert for HTTPS (enables HTTP/2 via ALPN)
var cert = useHttp ? null : (useFreshCert ? CreateFreshCert() : LoadDevCert());
var port = 7004;
var scheme = "http";
SslServerAuthenticationOptions? sslOptions = null;

if (cert is not null)
{
    sslOptions = new()
    {
        ServerCertificate = cert,
        EnabledSslProtocols =
            System.Security.Authentication.SslProtocols.Tls12
            | System.Security.Authentication.SslProtocols.Tls13,
        ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
    };
    scheme = "https";
    Console.Error.WriteLine($"Using {(useFreshCert ? "fresh runtime" : "dev")} certificate for HTTPS");
}
else
{
    var hint = useHttp ? "via --http" : "To enable HTTPS/HTTP2:  dotnet dev-certs https --trust";
    Console.Error.WriteLine($"No certificate found — using HTTP ({hint})");
}

await using var factory = new LoggerFactory(
    sinks: [new ColoredConsoleSink(new ConsoleFormatter())],
    options: new LoggerFactoryOptions
    {
        MinLevel = LogLevel.Warning,
        QueueCapacity = 65535,
        QueueFullMode = LogQueueFullMode.DropOldest,
        SyncWriteTimeout = TimeSpan.FromMilliseconds(250),
        ShutdownTimeout = TimeSpan.FromSeconds(5),
    }
);
var logger = factory.CreateLogger("PicoWeb.Sample");

var server = new WebServer(
    app,
    new WebServerOptions
    {
        // Use IPv6Any with DualMode so the socket accepts both IPv4 and IPv6 connections.
        // Modern browsers prefer IPv6 for localhost; without this they fail with ERR_CONNECTION_CLOSED.
        Endpoint = new IPEndPoint(IPAddress.IPv6Any, port),
        SslOptions = sslOptions,
        Logger = logger,
    }
);
await server.StartAsync();
Console.WriteLine($"Listening on {scheme}://localhost:{port}/");
Console.WriteLine("Open in your browser");
await Task.Delay(Timeout.Infinite);
await server.DisposeAsync();

static X509Certificate2 CreateFreshCert()
{
    var rsa = System.Security.Cryptography.RSA.Create(2048);
    var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
        "CN=localhost",
        rsa,
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
    req.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature
                | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment,
            critical: true));
    req.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            [new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], critical: true));
    var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    req.CertificateExtensions.Add(san.Build());

    // Keep the RSA alive until the cert is used to ensure the key remains accessible.
    // No PFX round-trip — SslStream/Schannel can use the ephemeral key directly
    // on Windows when the cert is loaded from the same process that created it.
    GC.KeepAlive(rsa);
    return req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
}

static X509Certificate2? LoadDevCert()
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
        X509Certificate2? best = null;
        foreach (var c in certs)
        {
            if (
                c.FriendlyName?.Contains("ASP.NET", StringComparison.OrdinalIgnoreCase) == true
                && c.NotAfter > DateTime.UtcNow
                && (best is null || c.NotAfter > best.NotAfter)
            )
            {
                best = c;
            }
        }
        return best;
    }
    catch { }
    return null;
}
