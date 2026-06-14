namespace PicoWeb;

public sealed class WebApiApp : IAsyncDisposable
{
    private readonly WebApp _app;
    private WebServer? _server;

    internal WebApiApp(WebApp app)
    {
        _app = app;
    }

    public WebApiApp MapGet(string pattern, Delegate handler)
    {
        _app.MapGet(pattern, handler);
        return this;
    }

    public WebApiApp MapPost(string pattern, Delegate handler)
    {
        _app.MapPost(pattern, handler);
        return this;
    }

    public WebApiApp MapPut(string pattern, Delegate handler)
    {
        _app.MapPut(pattern, handler);
        return this;
    }

    public WebApiApp MapDelete(string pattern, Delegate handler)
    {
        _app.MapDelete(pattern, handler);
        return this;
    }

    public async Task RunAsync(string uri, CancellationToken ct = default)
    {
        var ep = ParseEndpoint(uri);
        _server = new WebServer(_app, new WebServerOptions { Endpoint = ep });
        await _server.StartAsync(ct);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
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
        var port = u.Port > 0 ? u.Port : 80;

        if (host == "+" || host == "*")
            return new IPEndPoint(IPAddress.Any, port);

        if (!IPAddress.TryParse(host, out var addr))
            addr = Dns.GetHostEntry(host).AddressList[0];
        return new IPEndPoint(addr, port);
    }
}
