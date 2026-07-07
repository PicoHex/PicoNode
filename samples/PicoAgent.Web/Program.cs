var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

// Build config from settings.json or create bootstrap
var settingsPath = Path.Combine(homeDir, "settings.json");
if (!File.Exists(settingsPath))
{
    await File.WriteAllTextAsync(settingsPath,
        "{\"providers\":{},\"model\":\"\",\"thinkingEnabled\":true,\"thinkingLevel\":\"xhigh\",\"maxTokens\":4096}");
    Console.Error.WriteLine($"Created bootstrap config at {settingsPath}");
}

// Build Agent and Server
var config = new AgentConfig
{
    Providers = new Dictionary<string, ProviderEntry>
    {
        ["test"] = new() { ApiKey = "sk-test" },
    },
    Model = "test-model",
};
var factory = new AgentFactory().WithBuiltInTools();
var agent = factory.Build(config, homeDir);
var adapter = new LlmClientAdapter(new SimpleLlmClient());
await using var server = new PicoAgent.Server(agent, adapter, factory.GetToolRunner());
await server.ListenAsync("http://localhost:19998");

// Build frontend WebApp on port 9000
var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var container = new SvcContainer();
container.Build();
var app = new WebApp(container, new WebAppOptions { ServerHeader = "PicoAgent.Web" });
if (Directory.Exists(wwwroot))
    app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

var frontend = new WebServer(app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000) });
await frontend.StartAsync();
Console.WriteLine("PicoAgent.Web: frontend http://localhost:9000, backend http://localhost:19998");

await Task.Delay(-1);

// Simple mock LLM for demo
internal sealed class SimpleLlmClient : PicoNode.AI.ILLmClient
{
    public async IAsyncEnumerable<PicoNode.AI.AssistantMessageEvent> StreamAsync(
        PicoNode.AI.Model model, PicoNode.AI.Types.ChatContext ctx,
        PicoNode.AI.StreamOptions? opts, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new PicoNode.AI.AssistantMessageEvent.Done
        {
            Message = new PicoNode.AI.Message
            {
                Role = "assistant",
                ContentBlocks = [new PicoNode.AI.ContentBlock { Type = "text", Text = "Hello from PicoAgent.Web!" }],
                StopReason = "end_turn",
            },
        };
    }
}
