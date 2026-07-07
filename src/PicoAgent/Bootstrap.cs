using PicoNode.AI;

namespace PicoAgent;

public static class Bootstrap
{
    public static async Task<(Server server, AgentFactory factory)> StartAsync(
        string homeDir, string[] args, CancellationToken ct = default)
    {
        var config = await LoadConfigAsync(homeDir);
        var factory = new AgentFactory().WithBuiltInTools();

        if (config.Providers is null || config.Providers.Count == 0)
        {
            config.Providers = new Dictionary<string, ProviderEntry>
            {
                ["unconfigured"] = new() { ApiKey = "unconfigured", BaseUrl = "http://localhost" },
            };
            config.Model ??= "unconfigured";
        }
        var llmAdapter = BuildLlmAdapter(config);
        var server = new Server(agent, llmAdapter, factory.GetToolRunner());

        var uri = args.Length > 0 && args[0].StartsWith("http") ? args[0] : "http://localhost:8080";
        await server.ListenAsync(uri);
        return (server, factory);
    }

    public static ILlmClient BuildLlmAdapter(AgentConfig config)
    {
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var providerConfigs = new List<ProviderConfig>();
        var clients = new Dictionary<string, ILLmClient>();

        foreach (var (name, entry) in config.Providers)
        {
            var apiFormat = entry.ApiFormat?.ToLowerInvariant() switch
            {
                "anthropic" => AiApiFormat.AnthropicMessages,
                _ => AiApiFormat.OpenAIChatCompletions,
            };
            providerConfigs.Add(new ProviderConfig
            {
                Name = name, BaseUrl = entry.BaseUrl ?? "",
                ApiKey = entry.ApiKey, ApiFormat = apiFormat, Priority = 1,
            });
            clients[name] = apiFormat == AiApiFormat.AnthropicMessages
                ? new AnthropicLLmClient(http) : new OpenAILlmClient(http);
        }

        var router = new ProviderRouter(providerConfigs);
        var breakers = providerConfigs.ToDictionary(p => p.Name, _ => (ICircuitBreaker)new CircuitBreaker());
        var resilient = new ResilientLLmClient(router, providerConfigs.ToDictionary(p => p.Name), breakers, clients);
        return new LlmClientAdapter(resilient);
    }

    private static async Task<AgentConfig> LoadConfigAsync(string homeDir)
    {
        var path = Path.Combine(homeDir, "settings.json");
        if (!File.Exists(path))
        {
            var def = new AgentConfig
            {
                Providers = [],
                Model = "unconfigured",
                ThinkingEnabled = true,
                ThinkingLevel = AgentConfig.DefaultThinkingLevel.ToString().ToLowerInvariant(),
                MaxTokens = 4096,
            };
            var json = "{\"providers\":{},\"model\":\"unconfigured\",\"thinkingEnabled\":true,\"thinkingLevel\":\"xhigh\",\"maxTokens\":4096}";
            await File.WriteAllTextAsync(path, json);
            return def;
        }
        var raw = await File.ReadAllTextAsync(path);
        var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(Encoding.UTF8.GetBytes(raw));
        return config ?? new AgentConfig { Providers = [], Model = "" };
    }
}
