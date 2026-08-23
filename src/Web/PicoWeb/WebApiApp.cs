namespace PicoWeb;

public sealed class WebApiApp : IAsyncDisposable
{
    private readonly WebApp _app;
    private WebServer? _server;

    internal WebApiApp(WebApp app, PicoJetson.JsonOptions? jsonOptions = null)
    {
        _app = app;
        SerializationOptions = jsonOptions;
    }

    /// <summary>
    /// Per-instance JSON serialization options configured via
    /// <see cref="WebApiBuilder.ConfigureJson"/>. Instance-scoped — never a
    /// process-wide static — so multiple apps/tests cannot leak configuration.
    /// </summary>
    internal PicoJetson.JsonOptions? SerializationOptions { get; }

    /// <summary>The underlying <see cref="WebApp"/> — register routes and middleware on it.</summary>
    public WebApp App => _app;

    /// <summary>The effective <see cref="WebAppOptions"/> (observable for tests/tooling).</summary>
    internal WebAppOptions Options => _app.Options;

    public async Task RunAsync(string uri, CancellationToken ct = default)
    {
        var ep = ParseEndpoint(uri);
        _server = new WebServer(_app, new WebServerOptions { Endpoint = ep });
        await _server.StartAsync(ct);

        // Wait for Ctrl+C / process exit / cancellation, then stop gracefully.
        await ProcessShutdown.WaitAsync(ct);
        await _server.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    internal ITcpConnectionHandler GetHandler() => _app.Build();

    private static IPEndPoint ParseEndpoint(string uri)
    {
        var u = new Uri(uri);
        var host = u.Host;
        // Honour :0 explicitly — the caller wants an OS-assigned ephemeral port.
        // Only fall back to 80 when no port was specified at all (IsDefaultPort).
        var port = u.IsDefaultPort && u.Port == 80 ? 80 : u.Port;
        if (port < 0)
            port = 80;

        if (host == "+" || host == "*")
            return new IPEndPoint(IPAddress.Any, port);

        if (!IPAddress.TryParse(host, out var addr))
            addr = Dns.GetHostEntry(host).AddressList[0];
        return new IPEndPoint(addr, port);
    }
}
