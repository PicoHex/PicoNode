using PicoNode.Actor;
using PicoNode.Agent.Domain;

var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);

var settingsPath = Path.Combine(homeDir, "settings.json");
if (!File.Exists(settingsPath))
    await File.WriteAllTextAsync(settingsPath,
        """{"providers":{},"model":"unconfigured","thinkingEnabled":true,"thinkingLevel":"xhigh","maxTokens":4096}""");

var raw = await File.ReadAllTextAsync(settingsPath);
var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(Encoding.UTF8.GetBytes(raw))
    ?? new AgentConfig { Providers = [], Model = "unconfigured" };
if (config.Providers is null || config.Providers.Count == 0)
    config.Providers = new Dictionary<string, ProviderEntry> { ["unconfigured"] = new() { ApiKey = "unconfigured" } };
if (string.IsNullOrEmpty(config.Model)) config.Model = "unconfigured";

// Build LLM adapter
var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
var clients = new Dictionary<string, PicoNode.AI.ILLmClient>();
var pcs = new List<ProviderConfig>();
foreach (var (n, e) in config.Providers)
{
    var f = e.ApiFormat?.ToLowerInvariant() switch { "anthropic" => AiApiFormat.AnthropicMessages, _ => AiApiFormat.OpenAIChatCompletions };
    pcs.Add(new ProviderConfig { Name = n, BaseUrl = e.BaseUrl ?? "", ApiKey = e.ApiKey, ApiFormat = f, Priority = 1 });
    clients[n] = f == AiApiFormat.AnthropicMessages ? new AnthropicLLmClient(http) : new OpenAILlmClient(http);
}
var resilient = new ResilientLLmClient(new ProviderRouter(pcs), pcs.ToDictionary(p => p.Name),
    pcs.ToDictionary(p => p.Name, _ => (ICircuitBreaker)new CircuitBreaker()), clients);
var adapter = new LlmClientAdapter(resilient);

var system = new ActorSystem(new InMemoryEventStore());
var factory = new AgentFactory(system, adapter).WithBuiltInTools();
var agent = await factory.BuildAsync(config, homeDir);

var app = new WebApp(new PicoDI.SvcContainer(), new WebAppOptions { ServerHeader = "PicoAgent.Web" });
var server = new PicoAgent.Server(agent, system, adapter, factory.GetToolRunner());
PicoAgent.Server.AddEndpoints(app, agent, system, adapter, factory.GetToolRunner(), "/api", settingsPath);

var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot))
    app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

var ws = new WebServer(app, new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000) });
await ws.StartAsync();
Console.WriteLine("PicoAgent.Web on http://localhost:9000");
await Task.Delay(-1);
