var container = new SvcContainer();

// Session store
var sessionOptions = new SessionOptions
{
    IdleTimeout = TimeSpan.FromMinutes(30),
    CleanupInterval = TimeSpan.FromMinutes(5),
};
container.RegisterSingle(typeof(SessionOptions), sessionOptions);
container.RegisterSingleton<ISessionStore>(scope => new InMemorySessionStore(
    scope.GetService<SessionOptions>()
));

// Controllers are auto-registered by Controllers.Gen source generator
// via [ModuleInitializer] in ControllerServiceRegistrations.g.cs
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

await using var factory = new LoggerFactory(
    sinks: [new ColoredConsoleSink(new ConsoleFormatter())],
    options: new LoggerFactoryOptions
    {
        MinLevel = LogLevel.Trace,
        QueueCapacity = 65535,
        QueueFullMode = LogQueueFullMode.DropOldest,
        SyncWriteTimeout = TimeSpan.FromMilliseconds(250),
        ShutdownTimeout = TimeSpan.FromSeconds(5),
    }
);
var logger = factory.CreateLogger("PicoWeb.Sample");

var app = PicoWeb.Samples.Abs.ShowcaseApp.Create(
    container,
    webSocketHandler: wsEcho,
    logger: logger
);

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
    Console.Error.WriteLine(
        $"Using HTTPS with dev certificate: {cert.Subject}, key={cert.GetKeyAlgorithm()}"
    );
    sslOptions = new()
    {
        ServerCertificate = cert,
        EnabledSslProtocols =
            System.Security.Authentication.SslProtocols.Tls12
            | System.Security.Authentication.SslProtocols.Tls13,
        ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
    };
    scheme = "https";
    Console.Error.WriteLine(
        $"Using {(useFreshCert ? "fresh runtime" : "dev")} certificate for HTTPS"
    );
}
else
{
    var hint = useHttp ? "via --http" : "To enable HTTPS/HTTP2:  dotnet dev-certs https --trust";
    Console.Error.WriteLine($"No certificate found — using HTTP ({hint})");
}

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
Console.WriteLine("Press Ctrl+C to stop");

// Wait for Ctrl+C or process exit to properly close the listening socket.
var shutdownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

ConsoleCancelEventHandler cancelHandler = (sender, e) =>
{
    // First Ctrl+C: graceful shutdown; second Ctrl+C: force kill
    if (shutdownTcs.Task.IsCompleted)
        return;
    e.Cancel = true;
    shutdownTcs.TrySetResult();
};
EventHandler processExitHandler = (sender, e) => shutdownTcs.TrySetResult();

Console.CancelKeyPress += cancelHandler;
AppDomain.CurrentDomain.ProcessExit += processExitHandler;

try
{
    await Task.WhenAny(Task.Delay(Timeout.Infinite), shutdownTcs.Task);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
}

Console.WriteLine("Shutting down...");
await server.DisposeAsync();
Console.WriteLine("Server stopped. Port should be released.");

static X509Certificate2 CreateFreshCert()
{
    var rsa = System.Security.Cryptography.RSA.Create(2048);
    var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
        "CN=localhost",
        rsa,
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        System.Security.Cryptography.RSASignaturePadding.Pkcs1
    );
    // Do NOT add BasicConstraints extension.
    // A self-signed cert placed in the Root store with CA:FALSE is rejected by
    // Chromium-based browsers. Omitting BasicConstraints entirely lets the
    // browser accept it as a trust anchor (per RFC 5280 §4.2.1.9).
    req.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature
                | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment,
            critical: true
        )
    );
    req.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            [new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")],
            critical: true
        )
    );
    var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    req.CertificateExtensions.Add(san.Build());

    // Create cert, save to PFX file so Schannel can access the private key.
    var pfxPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PicoWeb",
        "localhost-dev-cert.pfx"
    );
    Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
    File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, ""));
    cert.Dispose();

    var loaded = X509CertificateLoader.LoadPkcs12FromFile(
        pfxPath,
        "",
        X509KeyStorageFlags.DefaultKeySet
    );

    // Best-effort trust: add to CurrentUser\Root so Schannel accepts HTTP/2 ALPN.
    try
    {
        using var rootStore = new X509Store("Root", StoreLocation.CurrentUser);
        rootStore.Open(OpenFlags.ReadWrite);
        var exists = false;
        foreach (var c in rootStore.Certificates)
        {
            if (c.Thumbprint == loaded.Thumbprint)
            {
                exists = true;
                break;
            }
        }
        if (!exists)
        {
            rootStore.Add(loaded);
            Console.Error.WriteLine($"Trusted dev cert in CurrentUser\\Root: {loaded.Subject}");
        }
        rootStore.Close();
    }
    catch
    { /* best-effort cleanup */
    }

    Console.Error.WriteLine(
        $"Created fresh dev cert: {loaded.Subject}, key={loaded.GetKeyAlgorithm()}"
    );
    return loaded;
}

static X509Certificate2? LoadDevCert()
{
    // Try to load the cached PFX first to avoid re-creating the cert
    // and re-triggering the UAC prompt on every restart.
    var pfxPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PicoWeb",
        "localhost-dev-cert.pfx"
    );

    if (File.Exists(pfxPath))
    {
        try
        {
            var cached = X509CertificateLoader.LoadPkcs12FromFile(
                pfxPath,
                "",
                X509KeyStorageFlags.DefaultKeySet
            );
            Console.Error.WriteLine(
                $"Loaded cached dev cert: {cached.Subject}, key={cached.GetKeyAlgorithm()}"
            );
            return cached;
        }
        catch
        {
            // Corrupted or invalid PFX — fall through to create a fresh one.
        }
    }

    return CreateFreshCert();
}
