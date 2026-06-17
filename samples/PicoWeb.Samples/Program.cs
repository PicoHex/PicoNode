using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using PicoDI;
using PicoDI.Abs;
using PicoNode.Http;
using PicoNode.Web;
using PicoWeb;

var container = new SvcContainer();
container.RegisterScoped<PicoWeb.Samples.Controllers.UsersController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.ProductsController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.PostsController>();
container.Build();

// WebSocket echo handler
WebSocketMessageHandler wsEcho = static async (msg, conn, ct) =>
{
    if (msg.OpCode == WebSocketOpCode.Close) return;
    var size = WebSocketFrameCodec.MeasureFrameSize(msg.Payload.Length, mask: false);
    var buf = new byte[size];
    WebSocketFrameCodec.WriteFrame(buf, msg.OpCode, msg.Payload.Span);
    await conn.SendAsync(new ReadOnlySequence<byte>(buf), ct);
};

var app = PicoWeb.Samples.ShowcaseApp.Create(container, webSocketHandler: wsEcho);
EndpointRegistrar.RegisterAll(app);

// Try to load ASP.NET Core dev cert for HTTPS (enables HTTP/2 via ALPN)
var cert = LoadDevCert();
var port = 7004;
var scheme = "http";
SslServerAuthenticationOptions? sslOptions = null;

if (cert is not null)
{
    sslOptions = new()
    {
        ServerCertificate = cert,
        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
        ApplicationProtocols = [ SslApplicationProtocol.Http11 ],
    };
    scheme = "https";
    Console.Error.WriteLine("Using dev certificate for HTTPS");
}
else
{
    Console.Error.WriteLine("No dev certificate found — using HTTP. To enable HTTPS/HTTP2:");
    Console.Error.WriteLine("  dotnet dev-certs https --trust");
}

var server = new WebServer(app, new WebServerOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, port),
    SslOptions = sslOptions,
});
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
        // ASP.NET Core dev cert has subject "CN=localhost"
        var certs = store.Certificates.Find(X509FindType.FindBySubjectName, "localhost", validOnly: false);
        foreach (var c in certs)
        {
            // Dev certs have a friendly name starting with "ASP.NET Core"
            if (c.FriendlyName?.Contains("ASP.NET", StringComparison.OrdinalIgnoreCase) == true)
                return c;
        }
    }
    catch { }
    return null;
}
