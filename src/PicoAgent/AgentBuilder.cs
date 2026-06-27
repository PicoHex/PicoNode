namespace PicoAgent;

public sealed class AgentBuilder
{
    private AgentConfig? _config;
    private HttpClient? _http;
    private string? _capabilitiesRoot;
    private CapabilityRegistry? _registry;

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

    public AgentHost BuildHost()
    {
        Validate();
        return BuildAgentHost();
    }

    public AgentServer BuildServer(AgentServerOptions options)
    {
        Validate();
        var host = BuildAgentHost();
        return new AgentServer(host, _registry!, _capabilitiesRoot ?? "", options);
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

    private AgentHost BuildAgentHost()
    {
        var http = _http ?? new HttpClient();
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

            providerConfigs.Add(new ProviderConfig
            {
                Name = name,
                BaseUrl = entry.BaseUrl ?? "",
                ApiFormat = apiFormat,
                ApiKey = entry.ApiKey,
                Priority = 1,
            });

            clients[name] = apiFormat == AiApiFormat.AnthropicMessages
                ? new AnthropicLLmClient(http)
                : new OpenAILlmClient(http);

            breakers[name] = new CircuitBreaker();
        }

        var router = new ProviderRouter(providerConfigs);
        var resilientClient = new ResilientLLmClient(router, breakers, clients);
        _registry = new CapabilityRegistry();

        if (_capabilitiesRoot is { Length: > 0 })
            _registry.Scan(_capabilitiesRoot);

        var runner = new CapabilityRunner();
        var model = ResolveModel(http, providerConfigs);
        var loop = new AgentLoop(resilientClient, _registry, runner, model);
        var host = new AgentHost(loop);

        if (_capabilitiesRoot is { Length: > 0 })
            TryRestoreSession(host, _capabilitiesRoot);

        return host;
    }

    private Model ResolveModel(HttpClient http, List<ProviderConfig> providerConfigs)
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
            // Sync-over-async is acceptable during startup (not on hot path).
            var discovered = ModelDiscovery
                .DiscoverAsync(http, pc, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
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

        if (defaultPreset?.Models is { } models && models.TryGetValue(modelId, out var modelOverride))
            AgentConfig.ApplyModelOverride(model, modelOverride);

        return model;
    }

    private static void TryRestoreSession(AgentHost host, string root)
    {
        try
        {
            var sessionPath = Path.Combine(root, "sessions", "default.jsonl");
            if (File.Exists(sessionPath))
            {
                var sessionData = SessionStore
                    .LoadAsync(sessionPath, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                host.RestoreSession("default", sessionData);
            }
        }
        catch
        {
            // Session restore is best-effort
        }
    }
}
