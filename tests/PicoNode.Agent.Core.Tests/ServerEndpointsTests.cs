namespace PicoNode.Agent.Tests;

public class ServerEndpointsTests
{
    [Test]
    public async Task Health_ReturnsOk() =>
        await AssertEndpoint("/health", async body => await Assert.That(body).Contains("ok"));

    [Test]
    public async Task Models_ReturnsArray() =>
        await AssertEndpoint("/models", async body => await Assert.That(body).StartsWith("["));

    [Test]
    public async Task Sessions_ReturnsArray() =>
        await AssertEndpoint("/sessions", async body => await Assert.That(body).StartsWith("["));

    [Test]
    public async Task ConfigStatus_ReturnsConfigured() =>
        await AssertEndpoint(
            "/config/status",
            async body => await Assert.That(body).Contains("configured")
        );

    [Test]
    public async Task ConfigProviders_ReturnsArray() =>
        await AssertEndpoint(
            "/config/providers",
            async body => await Assert.That(body).StartsWith("[")
        );

    [Test]
    public async Task SystemPrompt_Get() =>
        await AssertEndpoint(
            "/system-prompt",
            async body => await Assert.That(body).Contains("prompt")
        );

    private static async Task AssertEndpoint(string path, Func<string, Task> assert)
    {
        await using var server = await StartServer();
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{server.Port}/"),
        };
        var body = await http.GetStringAsync(path);
        await assert(body);
    }

    [Test]
    public async Task ApiProxy_ForwardsToBackend()
    {
        // Start backend on random port
        var config = new Domain.AgentConfig
        {
            Providers = new Dictionary<string, Domain.ProviderEntry>
            {
                ["test"] = new() { ApiKey = "sk-test" },
            },
            Model = "test-model",
        };
        var factory = new AgentFactory().WithBuiltInTools();
        var agent = factory.Build(config, "/tmp/proxy-test");
        var adapter = new LlmClientAdapter(new SimpleLlmClient());
        await using var backend = new PicoAgent.Server(agent, adapter, factory.GetToolRunner());
        await backend.ListenAsync("http://localhost:0");

        // Build frontend with proxy to backend
        var container = new SvcContainer();
        container.Build();
        var app = new WebApp(container, new WebAppOptions());

        var apiClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{backend.Port}/"),
        };
        app.Use(
            async (ctx, next, ct) =>
            {
                if (!ctx.Request.Path.StartsWith("/api/"))
                    return await next(ctx, ct);
                var path = ctx.Request.Path.Replace("/api", "");
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                var resp = await apiClient.SendAsync(req, ct);
                var body = await resp.Content.ReadAsByteArrayAsync(ct);
                return new HttpResponse
                {
                    StatusCode = 200,
                    Body = body,
                    Headers = [new("Content-Type", "application/json")],
                };
            }
        );

        var ep = new IPEndPoint(IPAddress.Loopback, 0);
        var ws = new WebServer(app, new WebServerOptions { Endpoint = ep });
        await ws.StartAsync();
        var frontendPort = ((IPEndPoint)ws.LocalEndPoint!).Port;

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{frontendPort}/"),
        };
        var body = await http.GetStringAsync("/api/health");
        await Assert.That(body).Contains("ok");

        await ws.DisposeAsync();
    }

    private static async Task<PicoAgent.Server> StartServer()
    {
        var config = new Domain.AgentConfig
        {
            Providers = new Dictionary<string, Domain.ProviderEntry>
            {
                ["test"] = new() { ApiKey = "sk-test" },
            },
            Model = "test-model",
        };
        var factory = new AgentFactory().WithBuiltInTools();
        var agent = factory.Build(config, "/tmp/test-ep");
        var adapter = new LlmClientAdapter(new SimpleLlmClient());
        var server = new PicoAgent.Server(agent, adapter, factory.GetToolRunner());
        await server.ListenAsync("http://localhost:0");
        return server;
    }
}
