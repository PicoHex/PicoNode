using PicoNode.Agent.Domain;

var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

var settingsPath = Path.Combine(homeDir, "settings.json");
if (!File.Exists(settingsPath))
{
    await File.WriteAllTextAsync(
        settingsPath,
        "{\"providers\":{},\"model\":\"unconfigured\",\"thinkingEnabled\":true,\"thinkingLevel\":\"xhigh\",\"maxTokens\":4096}"
    );
    Console.Error.WriteLine($"Created bootstrap config at {settingsPath}. Configure via web UI.");
}

var raw = await File.ReadAllTextAsync(settingsPath);
var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(Encoding.UTF8.GetBytes(raw));
if (config is null)
    config = new AgentConfig { Providers = [], Model = "unconfigured" };

// Ensure at least a dummy provider so Agent can start
if (config.Providers is null || config.Providers.Count == 0)
    config.Providers = new Dictionary<string, ProviderEntry>
    {
        ["unconfigured"] = new() { ApiKey = "unconfigured" },
    };
if (string.IsNullOrEmpty(config.Model))
    config.Model = "unconfigured";

var factory = new AgentFactory().WithBuiltInTools();
var agent = factory.Build(config, homeDir);
agent.Start();
var adapter = new LlmClientAdapter(new PicoAgent.DynamicLLmClient(agent));

var app = new WebApp(new SvcContainer(), new WebAppOptions { ServerHeader = "PicoAgent.Web" });
PicoAgent.Server.AddEndpoints(app, agent, adapter, factory.GetToolRunner(), "/api", settingsPath);

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
