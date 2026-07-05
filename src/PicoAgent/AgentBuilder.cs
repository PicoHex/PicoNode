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
    private ProviderRouter? _router;
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
        var http = _http ?? CreateHttpClient();
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
        _router = router;
        var resilientClient = new ResilientLLmClient(router, _providerConfigs, breakers, clients);
        _registry = new CapabilityRegistry();

        if (_capabilitiesRoot is { Length: > 0 })
            _registry.Scan(_capabilitiesRoot);

        var builtInTools = CreateBuiltInTools();

        var runner = new CapabilityRunner();
        var model = await ResolveModelAsync(http, providerConfigs, ct);
        _initialModel = model;
        var adapter = new AgentLlmAdapter(resilientClient, router);
        adapter.Tools = builtInTools.GetActiveToolSchemas();
        var loop = new AgentLoop(adapter, _registry, runner, builtInTools);
        loop.WorkingDirectory = _capabilitiesRoot ?? Directory.GetCurrentDirectory();

        // Build and set system prompt
        if (_capabilitiesRoot is { Length: > 0 })
        {
            // Load user-provided system prompt file (editable without recompilation)
            var customPromptPath = Path.Combine(_capabilitiesRoot, "system-prompt.md");
            var agentsMd = File.Exists(customPromptPath) ? File.ReadAllText(customPromptPath) : null;
            var skills = AgentBuilder.ScanSkills(_capabilitiesRoot);
            loop.SystemPrompt = SystemPromptBuilder.Build(skills, _registry.GetAll(), builtInTools, agentsMd);
        }

        var host = new AgentHost(loop);

        if (_capabilitiesRoot is { Length: > 0 })
            await TryRestoreSessionsAsync(host, _capabilitiesRoot, ct);

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
            BaseUrl = string.Empty,
            Api = apiFormat,
            Provider = defaultProvider,
            MaxTokens = _config.MaxTokens ?? 393216,
            ThinkingEnabled = _config.ThinkingEnabled,
            ThinkingLevel = AgentConfig.ParseLevel(_config.ThinkingLevel) ?? AgentConfig.DefaultThinkingLevel,
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

    private static async Task TryRestoreSessionsAsync(
        AgentHost host,
        string root,
        CancellationToken ct
    )
    {
        try
        {
            var sessionsDir = Path.Combine(root, "sessions");
            if (!Directory.Exists(sessionsDir))
                return;

            foreach (var sessionPath in Directory.EnumerateFiles(sessionsDir, "*.jsonl"))
            {
                try
                {
                    var sessionId = Path.GetFileNameWithoutExtension(sessionPath);
                    var storage = await JsonlSessionStorage.OpenAsync(sessionPath);
                    await host.RestoreSessionAsync(sessionId, new Session(storage));
                }
                catch
                {
                    // Individual session restore is best-effort
                }
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

    /// <summary>
    /// Build without requiring providers — used for unconfigured startup.
    /// Creates a noop AgentLoop so the server can start and serve the config page.
    /// </summary>
    internal async Task<AgentHost> BuildAgentHostUnconfiguredAsync()
    {
        var http = _http ?? CreateHttpClient();
        if (_http is null)
        {
            _http = http;
            _ownsHttpClient = true;
        }

        _providerConfigs = new Dictionary<string, ProviderConfig>();
        _breakers = new Dictionary<string, ICircuitBreaker>();
        _clients = new Dictionary<string, ILLmClient>();
        _router = new ProviderRouter([]);
        _registry = new CapabilityRegistry();

        if (_capabilitiesRoot is { Length: > 0 })
            _registry.Scan(_capabilitiesRoot);

        _initialModel = new Model
        {
            Id = string.Empty,
            Provider = "unknown",
            MaxTokens = _config?.MaxTokens ?? 393216,
        };

        var builtInTools = CreateBuiltInTools();
        var runner = new CapabilityRunner();
        var loop = new AgentLoop(new NoopAgentLlm(), _registry, runner, builtInTools);
        return new AgentHost(loop);
    }

    internal CapabilityRegistry GetRegistry() => _registry!;

    internal HttpClient GetHttpClient() => _http ?? new HttpClient();

    internal ProviderRouter GetRouter() => _router!;

    internal IReadOnlyDictionary<string, ProviderConfig> GetProviderConfigs() => _providerConfigs!;

    internal IReadOnlyDictionary<string, ICircuitBreaker> GetBreakers() => _breakers!;

    internal IReadOnlyDictionary<string, ILLmClient> GetClients() => _clients!;

    internal Model GetInitialModel() => _initialModel!;

    internal bool GetHttpClientIsOwned() => _ownsHttpClient;

    /// <summary>
    /// Creates an HttpClient that bypasses the system proxy.
    /// System proxies (e.g. 127.0.0.1:10808 from VPN tools) can silently
    /// break all outbound LLM API calls when the proxy process isn't running.
    /// </summary>
    private static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { UseProxy = false });

    internal static List<SkillInfo> ScanSkills(string homeDir)
    {
        var scanner = new KnowledgeScanner();
        var skills = new List<SkillInfo>();
        skills.AddRange(scanner.ScanFromDir(Path.Combine(homeDir, FileSystemConstants.SkillsDir)));
        skills.AddRange(scanner.ScanFromDir(Path.Combine(Directory.GetCurrentDirectory(), FileSystemConstants.ProjectSkillsDir)));
        skills.AddRange(scanner.Scan(homeDir));
        return skills;
    }

    internal static BuiltInToolSet CreateBuiltInTools()
    {
        var tools = new BuiltInToolSet();
        tools.Register(new ReadTool());
        tools.Register(new BashTool());
        tools.Register(new WriteTool());
        tools.Register(new EditTool());
        tools.Register(new GrepTool());
        tools.Register(new FindTool());
        tools.Register(new LsTool());
        tools.ApplyPreset(ToolPreset.All);
        return tools;
    }
}

/// <summary>
/// Noop IAgentLlm used when the Agent starts without any configured providers.
/// All requests return empty streams — the server is in setup mode.
/// </summary>
internal sealed class NoopAgentLlm : IAgentLlm
{
    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        string? systemPrompt,
        Message[] messages,
        string modelId,
        string? reasoningLevel,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield break;
    }
}
