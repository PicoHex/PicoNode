using PicoNode.Actor;
using PicoNode.Agent.Domain;

var home = new HomeDir(HomeDir.Resolve());
home.EnsureCreated();

var settingsPath = home.ConfigPath;
if (!File.Exists(settingsPath))
{
    await File.WriteAllTextAsync(
        settingsPath,
        """{"providers":{},"model":"unconfigured","thinkingEnabled":true,"thinkingLevel":"xhigh","maxTokens":4096}"""
    );
    Console.Error.WriteLine($"Created bootstrap config at {settingsPath}. Configure via web UI.");
}

var raw = await File.ReadAllTextAsync(settingsPath);
var config =
    PicoJetson.JsonSerializer.Deserialize<AgentConfig>(Encoding.UTF8.GetBytes(raw))
    ?? new AgentConfig { Providers = [], Model = "unconfigured" };

if (config.Providers is null || config.Providers.Count == 0)
    config.Providers = new Dictionary<string, ProviderEntry>
    {
        ["unconfigured"] = new() { ApiKey = "unconfigured" },
    };
if (string.IsNullOrEmpty(config.Model))
    config.Model = "unconfigured";

var system = new ActorSystem(new InMemoryEventStore());
var adapter = PicoAgent.Bootstrap.BuildLlmAdapter(config);
var factory = new AgentFactory(system, adapter).WithBuiltInTools();
var agent = await factory.BuildAsync(config);
system.Send(agent.Id, new StartAgent());

var app = new WebApp(new SvcContainer(), new WebAppOptions { ServerHeader = "PicoAgent.Web" });
PicoAgent.Server.AddEndpoints(
    app,
    agent,
    system,
    adapter,
    factory.GetToolRunner(),
    "/api",
    settingsPath
);

var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot))
    app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

var ws = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000) }
);
await ws.StartAsync();
Console.WriteLine("PicoAgent.Web on http://localhost:9000");
await Task.Delay(-1);
