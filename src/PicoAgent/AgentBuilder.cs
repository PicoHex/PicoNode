namespace PicoAgent;

public sealed class AgentBuilder
{
    private bool _ownsHttpClient; // true when AgentBuilder auto-created the HttpClient
    private AgentConfig? _config;
    private HttpClient? _http;
    private string? _capabilitiesRoot;
    private CapabilityRegistry? _registry;
    private Dictionary<string, ICircuitBreaker>? _breakers;
    private Dictionary<string, ILLmClient>? _clients;
    private Dictionary<string, ProviderConfig>? _providerConfigs;
    private Model? _initialModel;

    public AgentBuilder WithConfig(AgentConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        return this;
    }

    public AgentBuilder WithHttpClient(HttpClient http)
    {
        _http = http;
        return this;
    }

    public AgentBuilder WithCapabilities(string root)
    {
        _capabilitiesRoot = root;
        return this;
    }

    private void Validate()
    {
        if (_config is null)
            throw new InvalidOperationException(
                "No configuration provided. Call WithConfig() before Build()."
            );

        var validation = ConfigLoader.Validate(_config);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Configuration validation failed: {string.Join("; ", validation.Errors)}"
            );
    }

    private async Task<AgentHost> BuildAgentHostAsync(CancellationToken ct)
    {
        var http = _http ?? new HttpClient();
        if (_http is null)
        {
            _http = http;
            _ownsHttpClient = true;
        }
        var clients = new Dictionary<string, ILLmClient>();
        var breakers = new Dictionary<string, ICircuitBreaker>();
        var providerConfigs = new List<ProviderConfig>();

        foreach (var (name, entry) in _config!.Providers)
        {
            var apiFormat = entry.ApiFormat?.ToLowerInvariant() switch
            {
                "anthropic" => AiApiFormat.AnthropicMessages,
                "openai" => AiApiFormat.OpenAIChatCompletions,
                _ => AiApiFormat.OpenAIChatCompletions,
            };

            providerConfigs.Add(
                new ProviderConfig
                {
                    Name = name,
                    BaseUrl = entry.BaseUrl ?? "",
                    ApiFormat = apiFormat,
                    ApiKey = entry.ApiKey,
                    Priority = 1,
                }
            );

            clients[name] =
                apiFormat == AiApiFormat.AnthropicMessages
                    ? new AnthropicLLmClient(http)
                    : new OpenAILlmClient(http);

            breakers[name] = new CircuitBreaker();
        }

        _providerConfigs = providerConfigs.ToDictionary(p => p.Name, p => p);
        _breakers = breakers;
        _clients = clients;

        var router = new ProviderRouter(providerConfigs);
        var resilientClient = new ResilientLLmClient(router, breakers, clients);
        _registry = new CapabilityRegistry();

        if (_capabilitiesRoot is { Length: > 0 })
            _registry.Scan(_capabilitiesRoot);

        var runner = new CapabilityRunner();
        var model = await ResolveModelAsync(http, providerConfigs, ct);
        _initialModel = model;
        var loop = new AgentLoop(resilientClient, _registry, runner);
        var host = new AgentHost(loop);

        if (_capabilitiesRoot is { Length: > 0 })
            await TryRestoreSessionAsync(host, _capabilitiesRoot, ct);

        return host;
    }

    private async Task<Model> ResolveModelAsync(
        HttpClient http,
        List<ProviderConfig> providerConfigs,
        CancellationToken ct
    )
    {
        var defaultProvider = _config!.Providers.Keys.FirstOrDefault() ?? "unknown";
        var defaultPreset = _config.Providers.GetValueOrDefault(defaultProvider);
        var apiFormat = defaultPreset?.ApiFormat?.ToLowerInvariant() switch
        {
            "anthropic" => AiApiFormat.AnthropicMessages,
            _ => AiApiFormat.OpenAIChatCompletions,
        };

        string? modelId = _config.Model;
        if (modelId is null && defaultPreset is not null)
        {
            var pc = new ProviderConfig
            {
                Name = defaultProvider,
                BaseUrl = defaultPreset.BaseUrl ?? "",
                ApiFormat = apiFormat,
                ApiKey = defaultPreset.ApiKey,
                Priority = 1,
            };
            var discovered = await ModelDiscovery.DiscoverAsync(http, pc, ct);
            modelId = discovered.FirstOrDefault()?.Id;
        }

        modelId ??= "unknown";

        var model = new Model
        {
            Id = modelId,
            BaseUrl = "",
            Api = apiFormat,
            Provider = defaultProvider,
            MaxTokens = _config.MaxTokens ?? 4096,
            ThinkingEnabled = _config.ThinkingEnabled,
            ThinkingLevel = AgentConfig.ParseLevel(_config.ThinkingLevel) ?? ThinkingLevel.Medium,
        };

        if (defaultPreset?.Thinking is { Count: > 0 } map)
            model.ThinkingLevelMap = map;

        if (
            defaultPreset?.Models is { } models
            && models.TryGetValue(modelId, out var modelOverride)
        )
            AgentConfig.ApplyModelOverride(model, modelOverride);

        return model;
    }

    private static async Task TryRestoreSessionAsync(
        AgentHost host,
        string root,
        CancellationToken ct
    )
    {
        try
        {
            var sessionPath = Path.Combine(root, "sessions", "default.jsonl");
            if (File.Exists(sessionPath))
            {
                var session = await JsonlSessionStorage.OpenAsync(sessionPath);
                await host.RestoreSessionAsync("default", new Session(session));
            }
        }
        catch
        {
            // Session restore is best-effort
        }
    }

    // ── Internal accessors for Agent.CreateAsync ──

    internal async Task<AgentHost> BuildAgentHostInternalAsync()
    {
        Validate();
        return await BuildAgentHostAsync(CancellationToken.None);
    }

    internal CapabilityRegistry GetRegistry() => _registry!;

    internal HttpClient GetHttpClient() => _http ?? new HttpClient();

    internal IReadOnlyDictionary<string, ProviderConfig> GetProviderConfigs() => _providerConfigs!;

    internal IReadOnlyDictionary<string, ICircuitBreaker> GetBreakers() => _breakers!;

    internal IReadOnlyDictionary<string, ILLmClient> GetClients() => _clients!;

    internal Model GetInitialModel() => _initialModel!;

    internal bool GetHttpClientIsOwned() => _ownsHttpClient;
}
