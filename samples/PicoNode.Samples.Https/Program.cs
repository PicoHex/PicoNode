using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using PicoNode;
using PicoNode.Http;
using PicoNode.Web;
using PicoWeb;

// ────────────────────────────────────────────────────────────
// PicoNode HTTPS sample
//
// Demonstrates TLS configuration via two methods:
//   1. dotnet dev-certs (recommended for development)
//   2. X509Certificate2 from file (for CI/production)
//
// Usage:
//   dotnet dev-certs https --trust        # install dev-cert first
//   dotnet run --project samples/PicoNode.Samples.Https
//
//   curl -k https://localhost:5001/        # test with curl
//   curl --cacert ~/.dotnet/trust/ca.crt https://localhost:5001/
// ────────────────────────────────────────────────────────────

// ─── Try to load a development certificate ─────────────────

X509Certificate2? certificate = null;

// Method 1: dotnet dev-certs (Linux/macOS/Windows)
//   Stores the certificate at a well-known path.
//   The exact path varies by OS and .NET version.

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // Windows: dev-cert is in the OS certificate store
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly);
    var certs = store.Certificates.Find(
        X509FindType.FindBySubjectName,
        "localhost",
        validOnly: false);
    certificate = certs.FirstOrDefault();
    store.Close();
}
else
{
    // Linux/macOS: dev-cert path is ~/.dotnet/corefx/cryptography/x509store/
    // or can be exported via: dotnet dev-certs https --export-path server.pfx
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var certPath = Path.Combine(home, ".dotnet", "corefx", "cryptography", "x509store", "localhost.pfx");
    if (File.Exists(certPath))
    {
        certificate = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(certPath), null);
    }
}

// Method 2: Load from file (fallback)
if (certificate is null)
{
    var fallbackPath = "server.pfx";
    if (File.Exists(fallbackPath))
    {
        certificate = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(fallbackPath), null);
    }
}

if (certificate is null)
{
    Console.Error.WriteLine("No TLS certificate found.");
    Console.Error.WriteLine("Install a dev certificate:");
    Console.Error.WriteLine("  dotnet dev-certs https --trust");
    Console.Error.WriteLine("Or export to server.pfx:");
    Console.Error.WriteLine("  dotnet dev-certs https --export-path server.pfx");
    return 1;
}

// ─── Build the WebApp ─────────────────────────────────────

var app = new WebApp(new WebAppOptions
{
    ServerHeader = "PicoNode.Https",
});

app.Use(new SecurityHeadersMiddleware().InvokeAsync);

app.MapGet("/", static (_, _) =>
    ValueTask.FromResult(WebResults.Text(200, "Hello from PicoNode over HTTPS!", "OK")));

app.MapGet("/info", static (ctx, _) =>
{
    var tlsInfo = ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)
        ? proto : "https";
    return ValueTask.FromResult(
        WebResults.Json(200, $$"""{"protocol":"{{tlsInfo}}","tls":true}""", "OK"));
});

// ─── Start HTTPS server ───────────────────────────────────

var node = new TcpNode(new TcpNodeOptions
{
    Endpoint = new IPEndPoint(IPAddress.Loopback, 5001),
    ConnectionHandler = app.Build(),
    SslOptions = new SslServerAuthenticationOptions
    {
        ServerCertificate = certificate,
        // ALPN: negotiate HTTP/1.1 and HTTP/2 over TLS
        ApplicationProtocols =
        [
            SslApplicationProtocol.Http2,
            SslApplicationProtocol.Http11,
        ],
    },
});

await node.StartAsync();

Console.WriteLine($"HTTPS listening on {node.LocalEndPoint}");
Console.WriteLine();
Console.WriteLine("  https://localhost:5001/");
Console.WriteLine("  https://localhost:5001/info");
Console.WriteLine();
Console.WriteLine("Security headers + ALPN (h2 + http/1.1) enabled.");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();

await node.DisposeAsync();
return 0;
