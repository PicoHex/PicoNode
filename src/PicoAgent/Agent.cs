// src/PicoAgent/Agent.cs
namespace PicoAgent;

public sealed partial class Agent : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly AgentHost _host;
    private readonly CapabilityRegistry _registry;
    private readonly string? _homeDir;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ProviderRouter _router;
    private IReadOnlyDictionary<string, ProviderConfig> _providerConfigs;
    private IReadOnlyDictionary<string, ICircuitBreaker> _breakers;
    private IReadOnlyDictionary<string, ILLmClient> _clients;
    private readonly ILogger? _logger;
    private Model _pendingModel;
    private WebServer? _server;
    private bool _disposed;
    private bool _wasUnconfigured;

    private Agent(
        AgentHost host,
        CapabilityRegistry registry,
        string? homeDir,
        HttpClient http,
        bool ownsHttpClient,
        ProviderRouter router,
        IReadOnlyDictionary<string, ProviderConfig> providerConfigs,
        IReadOnlyDictionary<string, ICircuitBreaker> breakers,
        IReadOnlyDictionary<string, ILLmClient> clients,
        Model initialModel,
        ILogger? logger = null,
        bool wasUnconfigured = false
    )
    {
        _host = host;
        _registry = registry;
        _homeDir = homeDir;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _router = router;
        _providerConfigs = providerConfigs;
        _breakers = breakers;
        _clients = clients;
        _pendingModel = initialModel;
        _logger = logger;
        _wasUnconfigured = wasUnconfigured;
    }

    public static async Task<Agent> CreateAsync(
        AgentConfig config,
        string? homeDir = null,
        ILogger? logger = null
    )
    {
        var builder = new AgentBuilder().WithConfig(config);
        if (homeDir is { Length: > 0 })
            builder.WithCapabilities(homeDir);

        // Allow startup without providers — server runs in unconfigured/setup mode.
        var isEmpty = config.Providers is null || config.Providers.Count == 0;
        var host = isEmpty
            ? await builder.BuildAgentHostUnconfiguredAsync()
            : await builder.BuildAgentHostInternalAsync();

        var wasUnconfigured = isEmpty;

        var registry = builder.GetRegistry();
        var http = builder.GetHttpClient();
        var ownsHttp = builder.GetHttpClientIsOwned();
        var router = builder.GetRouter();
        var providerConfigs = builder.GetProviderConfigs();
        var breakers = builder.GetBreakers();
        var clients = builder.GetClients();
        var model = builder.GetInitialModel();

        return new Agent(
            host,
            registry,
            homeDir,
            http,
            ownsHttp,
            router,
            providerConfigs,
            breakers,
            clients,
            model,
            logger,
            wasUnconfigured
        );
    }

    public EndPoint? LocalEndPoint => _server?.LocalEndPoint;

    public async Task ListenAsync(string uri, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_server is not null)
            throw new InvalidOperationException("Already listening.");

        var ep = ParseEndpoint(uri);
        var app = BuildWebApp();
        var server = new WebServer(app, new WebServerOptions { Endpoint = ep, Logger = _logger });
        try
        {
            await server.StartAsync(ct);
            _server = server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
        _logger?.Info($"Agent listening on {_server.LocalEndPoint}");
    }

    public async Task RunAsync(string uri, CancellationToken ct = default)
    {
        await ListenAsync(uri, ct);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };
        EventHandler exitHandler = (_, _) => tcs.TrySetResult();
        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;
        // Also honour the caller's cancellation token — previously it was accepted
        // but only forwarded to ListenAsync, so a cancellation request after the
        // server started would hang RunAsync until Ctrl+C.
        using var ctReg = ct.Register(() => tcs.TrySetResult());
        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException) { }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
        }
        await StopAsync(CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_server is not null)
        {
            _logger?.Info("Agent stopping...");
            await _server.StopAsync(ct);
        }
    }

    public async Task<AgentResult> SendAsync(
        string sessionId,
        string message,
        CancellationToken ct = default
    )
    {
        if (_server is null)
            throw new InvalidOperationException("Not listening. Call ListenAsync first.");
        var model = SnapshotModel();
        var hostResult = await _host.ProcessMessageAsync(message, model, ct, sessionId);
        var allMsgs = await _host.GetSessionMessagesAsync(sessionId);
        return new AgentResult(hostResult, [.. allMsgs]);
    }

    public async Task<AgentResult> SendAsync(
        string sessionId,
        string message,
        Func<AssistantMessageEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken ct = default
    )
    {
        if (_server is null)
            throw new InvalidOperationException("Not listening. Call ListenAsync first.");
        var model = SnapshotModel();
        var hostResult = await _host.ProcessMessageAsync(message, model, ct, sessionId, onEvent);
        var allMsgs = await _host.GetSessionMessagesAsync(sessionId);
        return new AgentResult(hostResult, [.. allMsgs]);
    }

    public bool SwitchModel(string modelId)
    {
        lock (_lock)
        {
            _pendingModel.Id = modelId;
            return true;
        }
    }

    public bool SwitchProvider(string providerName)
    {
        lock (_lock)
        {
            if (!_providerConfigs.ContainsKey(providerName))
                return false;
            _pendingModel.Provider = providerName;
            return true;
        }
    }

    public void SwitchThinking(bool enabled, ThinkingLevel level)
    {
        lock (_lock)
        {
            _pendingModel.ThinkingEnabled = enabled;
            _pendingModel.ThinkingLevel = level;
        }
    }

    public string? GetSystemPrompt() => _host.GetSystemPrompt();

    public void SetSystemPrompt(string? prompt) => _host.SetSystemPrompt(prompt);

    internal AgentHost GetHostForTesting() => _host;

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(
        string? provider = null,
        CancellationToken ct = default
    )
    {
        if (provider is not null && _providerConfigs.TryGetValue(provider, out var pc))
            return await ModelDiscovery.DiscoverAsync(_http, pc, ct);
        var all = new List<DiscoveredModel>();
        foreach (var (_, pc2) in _providerConfigs)
            all.AddRange(await ModelDiscovery.DiscoverAsync(_http, pc2, ct));
        return all;
    }

    public async Task SaveAsync(string sessionId = "default", CancellationToken ct = default)
    {
        if (_homeDir is not { Length: > 0 })
            throw new InvalidOperationException("No homeDir configured.");
        var path = Path.Combine(_homeDir, "sessions", $"{sessionId}.jsonl");
        var session = _host.GetOrCreateSession(sessionId);
        var entries = await session.GetEntries();
        await JsonlSessionStorage.SaveAsync(path, entries, ct);
    }

    public async Task<IReadOnlyList<Message>> GetMessagesAsync(string sessionId = "default")
    {
        return await _host.GetSessionMessagesAsync(sessionId);
    }

    public async Task<List<string>> ListSessionsAsync(CancellationToken ct = default)
    {
        var sessions = new HashSet<string>(_host.GetActiveSessionIds());
        if (_homeDir is { Length: > 0 })
        {
            var sessionsDir = Path.Combine(_homeDir, "sessions");
            if (Directory.Exists(sessionsDir))
            {
                foreach (var file in Directory.GetFiles(sessionsDir, "*.jsonl"))
                    sessions.Add(Path.GetFileNameWithoutExtension(file)!);
            }
        }
        return sessions.ToList();
    }

    public Task CreateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _host.GetOrCreateSession(sessionId);
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_homeDir is not { Length: > 0 })
            throw new InvalidOperationException("No homeDir configured.");
        var path = Path.Combine(_homeDir, "sessions", $"{sessionId}.jsonl");
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<(
        CompactionEntry? Entry,
        int CompressedCount,
        long TokensSaved
    )> CompactSessionAsync(string sessionId, int keepRecent = 20, CancellationToken ct = default)
    {
        if (_clients.Count == 0)
            return (null, 0, 0);

        // Don't create a session just to check compaction eligibility.
        if (!_host.GetActiveSessionIds().Contains(sessionId))
            return (null, 0, 0);

        using var _ = await _host.LockSessionAsync(sessionId, ct);
        var session = _host.GetOrCreateSession(sessionId);

        var entries = await session.GetEntries();
        var path = entries.Where(e => e is not LeafEntry).ToArray();
        var msgInPath = path.OfType<MessageEntry>().Count();
        if (msgInPath <= keepRecent)
            return (null, 0, 0);

        var adapter = new AgentLlmAdapter(_clients.Values.First(), _router);
        var compactor = new Compactor(adapter);
        var settings = new CompactionSettings
        {
            Enabled = true,
            KeepRecentTokens = keepRecent * 256,
            ReserveTokens = 4096,
        };

        var result = await compactor.CompactAsync(session, settings, ct);
        if (result is null)
            return (null, 0, 0);

        await session.MoveTo(result.Id);

        var compressedCount = msgInPath - keepRecent;
        var summaryTokens = (result.Summary?.Length ?? 0) / 4;
        var tokensSaved = Math.Max(0, result.TokensBefore - summaryTokens);

        return (result, compressedCount, tokensSaved);
    }

    // ── Test support ──

    /// <summary>
    /// Creates an Agent wired to a pre-built AgentHost + mock dependencies for integration testing.
    /// </summary>
    internal static Agent CreateForTest(
        AgentHost host,
        CapabilityRegistry registry,
        Model initialModel,
        HttpClient? http = null,
        IReadOnlyDictionary<string, ProviderConfig>? providerConfigs = null,
        IReadOnlyDictionary<string, ICircuitBreaker>? breakers = null,
        IReadOnlyDictionary<string, ILLmClient>? clients = null
    )
    {
        http ??= new HttpClient();
        providerConfigs ??= new Dictionary<string, ProviderConfig>();
        breakers ??= new Dictionary<string, ICircuitBreaker>();
        clients ??= new Dictionary<string, ILLmClient>();
        var router = new ProviderRouter(providerConfigs.Values);
        return new Agent(
            host,
            registry,
            null,
            http,
            ownsHttpClient: true,
            router,
            providerConfigs,
            breakers,
            clients,
            initialModel
        );
    }

    // ── Private ──

    /// <summary>
    /// Design-review flaw #4: preserve existing <see cref="ICircuitBreaker"/> instances
    /// for provider names that survive a config reload. Only allocate a fresh
    /// <see cref="CircuitBreaker"/> for genuinely new provider names, and drop entries
    /// whose provider was removed. This prevents a hot-reload from silently resetting
    /// an Open circuit and re-flooding a known-failing provider.
    /// </summary>
    public static IReadOnlyDictionary<string, ICircuitBreaker> MergeBreakers(
        IReadOnlyDictionary<string, ICircuitBreaker> previous,
        IEnumerable<string> providerNames
    )
    {
        var merged = new Dictionary<string, ICircuitBreaker>();
        foreach (var name in providerNames)
        {
            merged[name] = previous.TryGetValue(name, out var existing)
                ? existing
                : new CircuitBreaker();
        }
        return merged;
    }

    /// <summary>
    /// Design-review flaw #15: previously ReloadProviderConfigs assigned the
    /// provider *name* (e.g. "deepseek") to <see cref="Model.Id"/> whenever
    /// <c>config.Model</c> was null, producing an invalid model identifier that
    /// the upstream provider would 404 on. Return the configured model when
    /// present and non-empty, otherwise return "" — the sentinel meaning
    /// "unresolved; downstream SwitchModel or first LLM call must fill this in".
    /// </summary>
    public static string ResolveInitialModelId(
        string? configModel,
        IReadOnlyList<ProviderConfig> providerList
    ) => configModel is { Length: > 0 } ? configModel : "";

    private void ReloadProviderConfigs(AgentConfig config)
    {
        var providerList = new List<ProviderConfig>();

        lock (_lock)
        {
            // Mutate existing dicts in-place (shared with ResilientLLmClient via reference)
            var pcDict = (Dictionary<string, ProviderConfig>)_providerConfigs;
            var brDict = (Dictionary<string, ICircuitBreaker>)_breakers;
            var clDict = (Dictionary<string, ILLmClient>)_clients;

            // Preserve breakers whose provider names survive; drop the rest.
            var mergedBreakers = MergeBreakers(brDict, config.Providers.Keys);

            pcDict.Clear();
            brDict.Clear();
            clDict.Clear();

            foreach (var (name, entry) in config.Providers)
            {
                var apiFormat = entry.ApiFormat?.ToLowerInvariant() switch
                {
                    "anthropic" => AiApiFormat.AnthropicMessages,
                    _ => AiApiFormat.OpenAIChatCompletions,
                };
                var pc = new ProviderConfig
                {
                    Name = name,
                    BaseUrl = entry.BaseUrl ?? "",
                    ApiFormat = apiFormat,
                    ApiKey = entry.ApiKey,
                    Priority = 1,
                };
                pcDict[name] = pc;
                providerList.Add(pc);
                brDict[name] = mergedBreakers[name];
                clDict[name] =
                    apiFormat == AiApiFormat.AnthropicMessages
                        ? new AnthropicLLmClient(_http)
                        : new OpenAILlmClient(_http);
            }

            _router.UpdateProviders(providerList);
            var wasUnconfigured = _wasUnconfigured;
            if (providerList.Count > 0)
            {
                _pendingModel.Provider = pcDict.Keys.First();
                _pendingModel.Api = providerList[0].ApiFormat;
                _pendingModel.Id = ResolveInitialModelId(config.Model, providerList);

                // When transitioning from unconfigured → configured, replace the
                // NoopAgentLlm loop with a real one wired through ResilientLLmClient
                // so subsequent messages route to the newly configured provider.
                // On normal hot-reload we keep the existing loop — the dicts are
                // mutated in-place and ResilientLLmClient already holds the live refs.
                if (wasUnconfigured)
                {
                    var resilientClient = new ResilientLLmClient(
                        _router,
                        _providerConfigs,
                        _breakers,
                        _clients
                    );
                    var adapter = new AgentLlmAdapter(resilientClient, _router);
                    var builtInTools = AgentBuilder.CreateBuiltInTools();
                    var newLoop = new AgentLoop(adapter, _registry, new CapabilityRunner(), builtInTools);
                    newLoop.WorkingDirectory = _homeDir ?? Directory.GetCurrentDirectory();
                    _host.ReplaceLoop(newLoop);
                    _wasUnconfigured = false;
                }
            }
        }
    }

    private Model SnapshotModel()
    {
        lock (_lock)
        {
            return new Model
            {
                Id = _pendingModel.Id,
                Provider = _pendingModel.Provider,
                BaseUrl = _pendingModel.BaseUrl,
                Api = _pendingModel.Api,
                ThinkingEnabled = _pendingModel.ThinkingEnabled,
                ThinkingLevel = _pendingModel.ThinkingLevel,
                ThinkingLevelMap = _pendingModel.ThinkingLevelMap,
                MaxTokens = _pendingModel.MaxTokens,
            };
        }
    }

    internal WebApp BuildWebApp()
    {
        var container = new SvcContainer();
        container.Build();
        var app = new WebApp(
            container,
            new WebAppOptions { ServerHeader = "PicoAgent", Logger = _logger }
        );

        // POST /session/{id}/message (async SSE)
        app.MapPost(
            "/session/{id}/message",
            async (ctx, ct) =>
            {
                var sessionId = ctx.RouteValues["id"] ?? "default";
                using var reader = new StreamReader(
                    ctx.Request.BodyStream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true
                );
                var input = await reader.ReadToEndAsync(ct);
                if (string.IsNullOrWhiteSpace(input))
                    return JsonError(400, "EMPTY_INPUT", "empty input");

                var model = SnapshotModel();
                var pipe = new Pipe();
                _ = WriteSseAsync(
                    new SseConnection(pipe.Writer),
                    pipe.Writer,
                    sessionId,
                    model,
                    input,
                    ct
                );
                return new HttpResponse
                {
                    StatusCode = 200,
                    Headers =
                    [
                        new("Content-Type", "text/event-stream"),
                        new("Cache-Control", "no-cache"),
                    ],
                    BodyStream = pipe.Reader.AsStream(),
                };
            }
        );

        // POST /model/switch (v1: always OK — routing validated at LLM call time)
        app.MapPost(
            "/model/switch",
            async (ctx, ct) =>
            {
                var req = await ReadJsonAsync<ModelSwitchReq>(ctx, ct);
                if (req is null || string.IsNullOrWhiteSpace(req.ModelId))
                    return JsonError(400, "INVALID_ARGUMENT", "modelId is required");
                SwitchModel(req.ModelId);
                return JsonOk();
            }
        );

        // POST /provider/switch (async, reads body)
        app.MapPost(
            "/provider/switch",
            async (ctx, ct) =>
            {
                var req = await ReadJsonAsync<ProviderSwitchReq>(ctx, ct);
                if (req is null || string.IsNullOrWhiteSpace(req.Provider))
                    return JsonError(400, "INVALID_ARGUMENT", "provider is required");
                return SwitchProvider(req.Provider)
                    ? JsonOk()
                    : JsonError(404, "NOT_FOUND", "Provider not configured");
            }
        );

        // POST /thinking (async, reads body)
        app.MapPost(
            "/thinking",
            async (ctx, ct) =>
            {
                var req = await ReadJsonAsync<ThinkingReq>(ctx, ct);
                var enabled = req?.Enabled ?? true;
                var levelStr = req?.Level ?? "medium";
                var level = AgentConfig.ParseLevel(levelStr);
                if (level is null)
                    return JsonError(400, "INVALID_ARGUMENT", $"Invalid level: {levelStr}");
                SwitchThinking(enabled, level.Value);
                return JsonOk();
            }
        );

        // GET /system-prompt
        app.MapGet(
            "/system-prompt",
            (_, _) =>
            {
                var prompt = GetSystemPrompt() ?? "";
                var dto = new SystemPromptResp { Prompt = prompt };
                var json = PicoJetson.JsonSerializer.Serialize(dto);
                return ValueTask.FromResult(JsonResponse(200, json));
            }
        );

        // PUT /system-prompt
        app.MapPost(
            "/system-prompt",
            async (ctx, ct) =>
            {
                var req = await ReadJsonAsync<SystemPromptReq>(ctx, ct);
                if (req?.Prompt is null)
                    return JsonError(400, "MISSING_FIELD", "prompt required");
                SetSystemPrompt(req.Prompt);
                return JsonOk();
            }
        );

        // POST /session/{id}/retry — remove last exchange before retrying
        app.MapPost(
            "/session/{id}/retry",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"] ?? "default";
                await _host.RetryLastMessageAsync(id);
                return JsonOk();
            }
        );

        // GET /models (async, ListModelsAsync)
        app.MapGet(
            "/models",
            async (ctx, ct) =>
            {
                var provider = ctx.Query.GetValueOrDefault("provider");
                var models = await ListModelsAsync(provider, ct);
                var json =
                    "["
                    + string.Join(
                        ",",
                        models.Select(m =>
                            $"{{\"id\":\"{EscapeJsonString(m.Id)}\",\"ownedBy\":\"{EscapeJsonString(m.OwnedBy)}\"}}"
                        )
                    )
                    + "]";
                return JsonResponse(200, json);
            }
        );

        // GET /health (sync) — manual JSON to avoid PicoJetson cross-assembly source-gen limitation
        app.MapGet(
            "/health",
            (_, _) =>
                ValueTask.FromResult(
                    new Func<HttpResponse>(() =>
                    {
                        var model = SnapshotModel();
                        return JsonResponse(
                            200,
                            $"{{\"status\":\"ok\",\"model\":\"{EscapeJsonString(model.Id)}\",\"provider\":\"{EscapeJsonString(model.Provider)}\"}}"
                        );
                    })()
                )
        );

        // GET /sessions (async)
        app.MapGet(
            "/sessions",
            async (_, ct) =>
            {
                var sessions = await ListSessionsAsync(ct);
                var json =
                    "["
                    + string.Join(",", sessions.Select(s => $"\"{EscapeJsonString(s)}\""))
                    + "]";
                return JsonResponse(200, json);
            }
        );

        // POST /session/{id}/create (sync)
        app.MapPost(
            "/session/{id}/create",
            (ctx, _) =>
                ValueTask.FromResult(
                    new Func<HttpResponse>(() =>
                    {
                        var id = ctx.RouteValues["id"] ?? "";
                        if (!SessionIdRegex().IsMatch(id))
                            return JsonError(400, "INVALID_SESSION_ID", id);
                        _host.GetOrCreateSession(id);
                        return JsonOk();
                    })()
                )
        );

        // POST /session/{id}/delete (sync)
        app.MapPost(
            "/session/{id}/delete",
            (ctx, _) =>
                ValueTask.FromResult(
                    new Func<HttpResponse>(() =>
                    {
                        var id = ctx.RouteValues["id"] ?? "";
                        if (_homeDir is not { Length: > 0 })
                            return JsonError(400, "NO_HOME_DIR", "homeDir not configured");
                        var path = Path.Combine(_homeDir, "sessions", $"{id}.jsonl");
                        if (!File.Exists(path))
                            return JsonError(404, "NOT_FOUND", $"Session not found: {id}");
                        File.Delete(path);
                        return JsonOk();
                    })()
                )
        );

        // GET /session/{id}/messages
        app.MapGet(
            "/session/{id}/messages",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"] ?? "";
                var msgs = await _host.GetSessionMessagesAsync(id);
                if (msgs.Count == 0 && id != "default")
                    return JsonError(404, "NOT_FOUND", $"Session not found: {id}");
                var json =
                    "["
                    + string.Join(",", msgs.Select(m => PicoJetson.JsonSerializer.Serialize(m)))
                    + "]";
                return JsonResponse(200, json);
            }
        );

        // POST /session/{id}/save (async)
        app.MapPost(
            "/session/{id}/save",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"] ?? "";
                if (_homeDir is not { Length: > 0 })
                    return JsonError(400, "NO_HOME_DIR", "homeDir not configured");
                await SaveAsync(id, ct);
                return JsonOk();
            }
        );

        // POST /session/{id}/compact (async)
        app.MapPost(
            "/session/{id}/compact",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"] ?? "default";
                var keepRecent = 20;
                try
                {
                    var req = await ReadJsonAsync<KeepRecentReq>(ctx, ct);
                    if (req is not null)
                        keepRecent = req.KeepRecent;
                }
                catch
                { /* body parsing best-effort — keep default */
                }

                var (entry, compressedCount, tokensSaved) = await CompactSessionAsync(
                    id,
                    keepRecent,
                    ct
                );
                if (entry is null)
                    return JsonResponse(
                        200,
                        "{\"compressedCount\":0,\"summary\":null,\"tokensSaved\":0}"
                    );

                var json =
                    "{"
                    + $"\"summary\":\"{EscapeJsonString(entry.Summary)}\","
                    + $"\"compressedCount\":{compressedCount},"
                    + $"\"tokensSaved\":{tokensSaved}"
                    + "}";
                return JsonResponse(200, json);
            }
        );

        // POST /reload (sync)
        app.MapPost(
            "/reload",
            (_, _) =>
                ValueTask.FromResult(
                    new Func<HttpResponse>(() =>
                    {
                        if (_homeDir is { Length: > 0 })
                        {
                            _registry.Scan(_homeDir);
                            _logger?.Info("Capabilities reloaded");
                        }
                        return JsonResponse(200, "{\"status\":\"reloaded\"}"u8.ToArray());
                    })()
                )
        );

        // ── Configuration management ──

        // GET /config/status — check if config is valid
        app.MapGet(
            "/config/status",
            (_, _) =>
            {
                var configured = _clients.Count > 0;
                var model = SnapshotModel();
                var status = new ConfigStatusResponse
                {
                    Configured = configured,
                    Model = model.Id,
                    Provider = model.Provider,
                    Providers = _providerConfigs.Keys.ToArray(),
                };
                var json = PicoJetson.JsonSerializer.Serialize(status);
                return ValueTask.FromResult(JsonResponse(200, json));
            }
        );

        // POST /config/validate — test a provider config without saving
        app.MapPost(
            "/config/validate",
            async (ctx, ct) =>
            {
                var req = await ReadJsonAsync<ConfigValidateReq>(ctx, ct);
                if (
                    req is null
                    || string.IsNullOrWhiteSpace(req.Provider)
                    || string.IsNullOrWhiteSpace(req.ApiKey)
                )
                    return JsonError(400, "INVALID_ARGUMENT", "provider and apiKey required");
                try
                {
                    var models = await ValidateProviderAsync(
                        req.Provider,
                        req.ApiKey,
                        req.BaseUrl,
                        req.ApiFormat ?? "openai",
                        ct
                    );
                    var result =
                        "["
                        + string.Join(
                            ",",
                            models.Select(m =>
                                $"{{\"id\":\"{EscapeJsonString(m.Id)}\",\"ownedBy\":\"{EscapeJsonString(m.OwnedBy)}\"}}"
                            )
                        )
                        + "]";
                    return JsonResponse(200, result);
                }
                catch (Exception ex)
                {
                    // NEVER echo ex.Message back to the caller — provider /
                    // HttpClient exception messages routinely contain internal
                    // URLs, IPs, hostnames and occasionally header fragments.
                    // The endpoint is unauthenticated in the current design,
                    // so any passthrough is an information disclosure. Log
                    // full detail server-side, return a fixed short string.
                    _logger?.Error(
                        $"/config/validate failed for provider={req.Provider}: {ex.Message}",
                        ex
                    );
                    return JsonError(
                        400,
                        "VALIDATION_FAILED",
                        "Provider validation failed. Check server logs for details."
                    );
                }
            }
        );

        // POST /config — save config and reload
        app.MapPost(
            "/config",
            async (ctx, ct) =>
            {
                if (_homeDir is not { Length: > 0 })
                    return JsonError(400, "NO_HOME_DIR", "homeDir not configured");
                var raw = await ReadBodyTextAsync(ctx, ct);
                if (string.IsNullOrWhiteSpace(raw))
                    return JsonError(400, "INVALID_ARGUMENT", "config body required");
                var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(
                    Encoding.UTF8.GetBytes(raw)
                );
                if (config is null)
                    return JsonError(400, "INVALID_ARGUMENT", "malformed config");
                var path = Path.Combine(_homeDir, "settings.json");
                await ConfigLoader.SaveAsync(path, config, ct);
                ReloadProviderConfigs(config);
                _logger?.Info("Configuration saved and reloaded");
                return JsonResponse(200, "{\"status\":\"saved\"}");
            }
        );

        // GET /config/providers — list known provider templates for the UI
        app.MapGet(
            "/config/providers",
            (_, _) => ValueTask.FromResult(JsonResponse(200, ProviderTemplates))
        );

        return app;
    }

    private static async Task<T?> ReadJsonAsync<T>(WebContext ctx, CancellationToken ct)
        where T : class
    {
        var text = await ReadBodyTextAsync(ctx, ct);
        if (text is null)
            return null;
        return PicoJetson.JsonSerializer.Deserialize<T>(Encoding.UTF8.GetBytes(text));
    }

    private static async Task<string?> ReadBodyTextAsync(WebContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(
            ctx.Request.BodyStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true
        );
        var text = await reader.ReadToEndAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string EscapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private async Task<List<DiscoveredModel>> ValidateProviderAsync(
        string name,
        string apiKey,
        string? baseUrl,
        string apiFormat,
        CancellationToken ct
    )
    {
        var fmt = apiFormat.ToLowerInvariant() switch
        {
            "anthropic" => AiApiFormat.AnthropicMessages,
            _ => AiApiFormat.OpenAIChatCompletions,
        };
        var pc = new ProviderConfig
        {
            Name = name,
            ApiKey = apiKey,
            ApiFormat = fmt,
            BaseUrl =
                baseUrl
                ?? (
                    fmt == AiApiFormat.AnthropicMessages
                        ? "https://api.anthropic.com"
                        : "https://api.openai.com/v1"
                ),
        };
        var result = await ModelDiscovery.DiscoverAsync(_http, pc, ct);
        return result switch
        {
            { Length: 0 } => throw new InvalidOperationException(
                "No models discovered — check API key and base URL"
            ),
            var models => models.ToList(),
        };
    }

    private static readonly string ProviderTemplates = """
        [
          {"name":"openai","label":"OpenAI","baseUrl":"https://api.openai.com/v1","apiFormat":"openai"},
          {"name":"anthropic","label":"Anthropic","baseUrl":"https://api.anthropic.com","apiFormat":"anthropic"},
          {"name":"deepseek","label":"DeepSeek","baseUrl":"https://api.deepseek.com/v1","apiFormat":"openai"},
          {"name":"kimi","label":"Moonshot (Kimi)","baseUrl":"https://api.moonshot.cn/v1","apiFormat":"openai"},
          {"name":"groq","label":"Groq","baseUrl":"https://api.groq.com/openai/v1","apiFormat":"openai"},
          {"name":"glm","label":"Zhipu (GLM)","baseUrl":"https://open.bigmodel.cn/api/paas/v4","apiFormat":"openai"},
          {"name":"ollama","label":"Ollama (local)","baseUrl":"http://localhost:11434/v1","apiFormat":"openai"}
        ]
        """;

    private static HttpResponse JsonOk() =>
        new()
        {
            StatusCode = 200,
            Body = "{\"status\":\"ok\"}"u8.ToArray(),
            Headers = [new("Content-Type", "application/json; charset=utf-8")],
        };

    private static HttpResponse JsonError(int code, string errorCode, string message) =>
        new()
        {
            StatusCode = code,
            Body = Encoding.UTF8.GetBytes(
                $"{{\"type\":\"error\",\"code\":\"{EscapeJsonString(errorCode)}\",\"message\":\"{EscapeJsonString(message)}\"}}"
            ),
            Headers = [new("Content-Type", "application/json; charset=utf-8")],
        };

    private static HttpResponse JsonResponse(int code, string json) =>
        JsonResponse(code, Encoding.UTF8.GetBytes(json));

    private static HttpResponse JsonResponse(int code, byte[] body) =>
        new()
        {
            StatusCode = code,
            Body = body,
            Headers = [new("Content-Type", "application/json; charset=utf-8")],
        };

    private async Task WriteSseAsync(
        SseConnection sse,
        PipeWriter writer,
        string sessionId,
        Model model,
        string input,
        CancellationToken ct
    )
    {
        try
        {
            await _host.ProcessMessageAsync(
                input,
                model,
                ct,
                sessionId,
                onEvent: async (evt, ct2) =>
                {
                    switch (evt)
                    {
                        case AssistantMessageEvent.TextDelta td:
                            await sse.WriteJsonAsync(
                                $"{{\"type\":\"delta\",\"content\":\"{EscapeJsonString(td.Delta)}\"}}",
                                ct2
                            );
                            break;
                        case AssistantMessageEvent.ThinkingDelta th:
                            await sse.WriteJsonAsync(
                                $"{{\"type\":\"thinking\",\"content\":\"{EscapeJsonString(th.Delta)}\"}}",
                                ct2
                            );
                            break;
                        case AssistantMessageEvent.ToolCallStart ts:
                        {
                            var b = ts.Partial.ContentBlocks?.ElementAtOrDefault(ts.Index);
                            await sse.WriteJsonAsync(
                                $"{{\"type\":\"tool_call_start\",\"toolCallId\":\"{EscapeJsonString(b?.Id ?? "")}\",\"toolName\":\"{EscapeJsonString(b?.Name ?? "")}\"}}",
                                ct2
                            );
                            break;
                        }
                        case AssistantMessageEvent.ToolCallDelta td2:
                        {
                            var b = td2.Partial.ContentBlocks?.ElementAtOrDefault(td2.Index);
                            await sse.WriteJsonAsync(
                                $"{{\"type\":\"tool_call_delta\",\"toolCallId\":\"{EscapeJsonString(b?.Id ?? "")}\",\"content\":\"{EscapeJsonString(td2.Delta)}\"}}",
                                ct2
                            );
                            break;
                        }
                        case AssistantMessageEvent.ToolCallEnd te:
                            await sse.WriteJsonAsync(
                                $"{{\"type\":\"tool_call_end\",\"toolCallId\":\"{EscapeJsonString(te.Call.Id ?? "")}\"}}",
                                ct2
                            );
                            break;
                        case AssistantMessageEvent.Done d:
                            await sse.WriteJsonAsync(
                                $"{{\"type\":\"done\",\"stopReason\":\"{EscapeJsonString(d.Message.StopReason ?? "")}\"}}",
                                ct2
                            );
                            break;
                        case AssistantMessageEvent.Error e:
                            await sse.WriteJsonAsync(
                                $"{{\"type\":\"error\",\"message\":\"{EscapeJsonString(e.Message.ErrorMessage ?? "")}\"}}",
                                ct2
                            );
                            break;
                    }
                }
            );
            await sse.CompleteAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            _logger?.Error($"SSE stream error for session {sessionId}: {ex.Message}", ex);
            await writer.CompleteAsync(ex);
        }
    }

    private static IPEndPoint ParseEndpoint(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u))
            throw new ArgumentException($"Invalid URI: {uri}", nameof(uri));
        // Honour explicitly-supplied port (including :0 for OS-assigned
        // ephemeral). Only default to 80 when the caller left the port off
        // (Uri.IsDefaultPort) or the URI parser returned a negative value.
        var port = u.IsDefaultPort || u.Port < 0 ? 80 : u.Port;
        if (u.Host is "localhost" or "127.0.0.1" or "::1")
            return new IPEndPoint(IPAddress.Loopback, port);
        if (IPAddress.TryParse(u.Host, out var addr))
            return new IPEndPoint(addr, port);
        return new IPEndPoint(IPAddress.Loopback, port);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex SessionIdRegex();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_server is not null)
        {
            _logger?.Info("Agent disposing...");
            await _server.DisposeAsync();
            _server = null;
        }
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
