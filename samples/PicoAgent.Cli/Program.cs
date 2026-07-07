using PicoNode.Agent.Domain;

var homeDir = ResolveHomeDir();
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

var settingsPath = Path.Combine(homeDir, "settings.json");
if (!File.Exists(settingsPath))
{
    settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    if (!File.Exists(settingsPath))
        settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");
}

// Config loading and LlmClient wiring need infrastructure (PicoNode.Agent.ConfigLoader, etc.)
// For now, use a minimal config and mock LLM
Console.WriteLine("PicoAgent CLI — using mock LLM for testing");
Console.WriteLine($"Home: {homeDir}");

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8080;
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
var adapter = new LlmClientAdapter(new PicoNode.Agent.Tests.SimpleLlmClient());
await using var server = new PicoAgent.Server(agent, adapter, factory.GetToolRunner());
await server.ListenAsync($"http://localhost:{port}");
Console.WriteLine($"Listening on http://localhost:{port}");

await Task.Delay(-1); // run forever

static string ResolveHomeDir()
{
    var portable = Path.Combine(Directory.GetCurrentDirectory(), "data");
    if (Directory.Exists(portable)) return Path.GetFullPath(portable);
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pico-agent"
    );
}
