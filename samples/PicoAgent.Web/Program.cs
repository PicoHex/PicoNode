using PicoNode.Agent.Domain;

var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);

var config = new AgentConfig { Providers = new Dictionary<string, ProviderEntry> { ["test"] = new() { ApiKey = "sk-test" } }, Model = "test" };
if (Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") is { Length: > 0 } key)
    config = new AgentConfig { Providers = new Dictionary<string, ProviderEntry> { ["deepseek"] = new() { ApiKey = key, BaseUrl = "https://api.deepseek.com/v1" } }, Model = "deepseek-chat" };

var factory = new AgentFactory().WithBuiltInTools();
var agent = factory.Build(config, homeDir);
agent.Start();
var adapter = PicoAgent.Bootstrap.BuildLlmAdapter(config);

// Single WebApp: API routes + static files on port 9000
var app = new WebApp(new SvcContainer(), new WebAppOptions { ServerHeader = "PicoAgent.Web" });
PicoAgent.Server.AddEndpoints(app, agent, adapter, factory.GetToolRunner(), "/api");

var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot)) app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

var ws = new WebServer(app, new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000) });
await ws.StartAsync();
Console.WriteLine("PicoAgent.Web on http://localhost:9000");
await Task.Delay(-1);
