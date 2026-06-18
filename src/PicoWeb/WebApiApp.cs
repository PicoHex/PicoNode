namespace PicoWeb;

public sealed class WebApiApp : IAsyncDisposable
{
    private readonly WebApp _app;
    private WebServer? _server;

    internal WebApiApp(WebApp app)
    {
        _app = app;
    }

    public WebApiApp MapGet(string pattern, WebRequestHandler handler)
    {
        _app.MapGet(pattern, handler);
        return this;
    }

    public WebApiApp MapPost(string pattern, WebRequestHandler handler)
    {
        _app.MapPost(pattern, handler);
        return this;
    }

    public WebApiApp MapPut(string pattern, WebRequestHandler handler)
    {
        _app.MapPut(pattern, handler);
        return this;
    }

    public WebApiApp MapDelete(string pattern, WebRequestHandler handler)
    {
        _app.MapDelete(pattern, handler);
        return this;
    }

    public async Task RunAsync(string uri, CancellationToken ct = default)
    {
        var ep = ParseEndpoint(uri);
        _server = new WebServer(_app, new WebServerOptions { Endpoint = ep });
        await _server.StartAsync(ct);

        // Register process exit signals for graceful shutdown
        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var shutdownTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        ConsoleCancelEventHandler cancelHandler = (sender, e) =>
        {
            e.Cancel = true;
            shutdownTcs.TrySetResult();
        };
        EventHandler processExitHandler = (sender, e) =>
        {
            shutdownTcs.TrySetResult();
        };

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            await Task.WhenAny(Task.Delay(Timeout.Infinite, shutdownCts.Token), shutdownTcs.Task);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown via cancellation token
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        }

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
        var port = u.Port > 0 ? u.Port : 80;

        if (host == "+" || host == "*")
            return new IPEndPoint(IPAddress.Any, port);

        if (!IPAddress.TryParse(host, out var addr))
            addr = Dns.GetHostEntry(host).AddressList[0];
        return new IPEndPoint(addr, port);
    }
}
