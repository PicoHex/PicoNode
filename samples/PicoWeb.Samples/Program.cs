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

// Use --http to force plain HTTP (bypass browser certificate issues)
var useHttp = args.Contains("--http");

// Try to load ASP.NET Core dev cert for HTTPS (enables HTTP/2 via ALPN)
var cert = useHttp ? null : LoadDevCert();
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
    Console.Error.WriteLine("Using dev certificate for HTTPS");
}
else
{
    var hint = useHttp ? "via --http" : "To enable HTTPS/HTTP2:  dotnet dev-certs https --trust";
    Console.Error.WriteLine($"No dev certificate found — using HTTP ({hint})");
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
        Endpoint = new IPEndPoint(IPAddress.Loopback, port),
        SslOptions = sslOptions,
        Logger = logger,
    }
);
await server.StartAsync();
Console.WriteLine($"Listening on {scheme}://localhost:{port}/");
Console.WriteLine("Open in your browser");
await Task.Delay(Timeout.Infinite);
await server.DisposeAsync();

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
