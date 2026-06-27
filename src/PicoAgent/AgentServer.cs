namespace PicoAgent;


public sealed class AgentServer : IAsyncDisposable
{
    private readonly AgentHost _host;
    private readonly CapabilityRegistry _registry;
    private readonly string _capabilitiesRoot;
    private readonly AgentServerOptions _options;
    private WebServer? _server;
    private bool _disposed;

    public AgentServer(
        AgentHost host,
        CapabilityRegistry registry,
        string capabilitiesRoot,
        AgentServerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        _host = host;
        _registry = registry;
        _capabilitiesRoot = capabilitiesRoot ?? "";
        _options = options;
    }

    public EndPoint? LocalEndPoint => _server?.LocalEndPoint;

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_server is not null)
            throw new InvalidOperationException("Server has already been started.");

        var app = BuildWebApp();

        _server = new WebServer(
            app,
            new WebServerOptions
            {
                Endpoint = _options.Endpoint,
                Logger = _options.Logger,
                SslOptions = _options.SslOptions,
                EnableDualMode = _options.EnableDualMode,
                MaxConnections = _options.MaxConnections,
                ReceiveSocketBufferSize = _options.ReceiveSocketBufferSize,
                SendSocketBufferSize = _options.SendSocketBufferSize,
                NoDelay = _options.NoDelay,
                IdleTimeout = _options.IdleTimeout,
                DrainTimeout = _options.DrainTimeout,
                AcceptFaultBackoff = _options.AcceptFaultBackoff,
            }
        );

        await _server.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_server is not null)
            await _server.StopAsync(ct);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_server is not null)
            throw new InvalidOperationException(
                "Server has already been started. Use StartAsync/StopAsync for manual control, or call RunAsync only once."
            );

        var app = BuildWebApp();

        _server = new WebServer(
            app,
            new WebServerOptions
            {
                Endpoint = _options.Endpoint,
                Logger = _options.Logger,
                SslOptions = _options.SslOptions,
                EnableDualMode = _options.EnableDualMode,
                MaxConnections = _options.MaxConnections,
                ReceiveSocketBufferSize = _options.ReceiveSocketBufferSize,
                SendSocketBufferSize = _options.SendSocketBufferSize,
                NoDelay = _options.NoDelay,
                IdleTimeout = _options.IdleTimeout,
                DrainTimeout = _options.DrainTimeout,
                AcceptFaultBackoff = _options.AcceptFaultBackoff,
            }
        );

        await _server.StartAsync(ct);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };
        EventHandler exitHandler = (_, _) => tcs.TrySetResult();

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException) { }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
        }

        await StopAsync(CancellationToken.None);
    }

    private WebApp BuildWebApp()
    {
        var container = new SvcContainer();
        container.Build();

        var app = new WebApp(container, new WebAppOptions
        {
            ServerHeader = _options.ServerHeader,
        });

        app.MapPost(
            "/session/{id}/message",
            AgentEndpoints.CreateMessageHandler(_host)
        );

        app.MapPost(
            "/reload",
            AgentEndpoints.CreateReloadHandler(_registry, _capabilitiesRoot)
        );

        return app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
