namespace PicoAgent;

internal static class JsonHelper
{
    public static HttpResponse JsonResponse<T>(T value, int statusCode = 200) =>
        new()
        {
            StatusCode = statusCode,
            Body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),
            Headers = [new("Content-Type", "application/json; charset=utf-8")],
        };

    public static HttpResponse Ok() => JsonResponse(new StatusResponse { Status = "ok" });

    public static HttpResponse Error(int code, string msg) =>
        JsonResponse(new ErrorResponse { Error = msg }, code);

    public static HttpResponse RawJson(string json, int statusCode = 200) =>
        new()
        {
            StatusCode = statusCode,
            Body = Encoding.UTF8.GetBytes(json),
            Headers = [new("Content-Type", "application/json; charset=utf-8")],
        };
}

public sealed class Server : IAsyncDisposable
{
    private readonly Agent _agent;
    private readonly ActorSystem _agentSystem;
    private readonly IActorSystem _sessionSystem;
    private readonly RuntimeActor _runtime;
    private readonly ILlmClient _llmClient;
    private readonly IToolRunner _toolRunner;
    private readonly ILogger? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly string? _settingsPath;
    private WebServer? _webServer;
    private bool _isListening;
    private readonly PerSessionLock _turnLock = new();

    private string? _agentNameCached;
    private AgentConfigSnapshot? _configSnapshotCached;
    private string AgentName => _agentNameCached ??= ResolveAgentName();

    private string ResolveAgentName()
    {
        try
        {
            var snap = _agentSystem
                .AskAsync<AgentConfigSnapshot>(_agent.Id, new GetConfigQuery())
                .GetAwaiter()
                .GetResult();
            return snap?.Name ?? "Unnamed";
        }
        catch
        {
            return "Unnamed";
        }
    }

    private AgentConfigSnapshot GetConfigSnapshot()
    {
        if (_configSnapshotCached is not null)
            return _configSnapshotCached;
        _configSnapshotCached = _agentSystem
            .AskAsync<AgentConfigSnapshot>(_agent.Id, new GetConfigQuery())
            .GetAwaiter()
            .GetResult();
        return _configSnapshotCached;
    }

    public int Port => _webServer?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;

    public Server(
        Agent agent,
        ActorSystem agentSystem,
        IActorSystem sessionSystem,
        RuntimeActor runtime,
        ILlmClient llmClient,
        IToolRunner toolRunner,
        ILogger? logger = null,
        ILoggerFactory? loggerFactory = null,
        string? settingsPath = null
    )
    {
        _agent = agent;
        _agentSystem = agentSystem;
        _sessionSystem = sessionSystem;
        _runtime = runtime;
        _llmClient = llmClient;
        _toolRunner = toolRunner;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settingsPath = settingsPath;
    }

    public async Task ListenAsync(string uri)
    {
        if (_isListening)
            throw new InvalidOperationException("Already listening");
        _isListening = true;
        if (_agent.Status == AgentStatus.Pending)
            _agentSystem.Send(_agent.Id, new StartAgent());
        _webServer = new WebServer(
            BuildWebApp(),
            new WebServerOptions { Endpoint = ParseEndpoint(uri) }
        );
        await _webServer.StartAsync(CancellationToken.None);
    }

    public void AddEndpoints(WebApp app, string prefix = "/api")
    {
        string? sp = null;
        var p = prefix;

        // Health
        app.MapGet(
            $"{p}/health",
            (_, _) =>
                V(
                    JsonHelper.JsonResponse(
                        new HealthResponse
                        {
                            Status = "ok",
                            Model = _agent.CurrentLlm.ModelId,
                            Provider = _agent.CurrentLlm.ProviderName,
                        }
                    )
                )
        );

        // Models (unchanged logic, uses instance fields)
        app.MapGet(
            $"{p}/models",
            async (_, ct) =>
            {
                var all = new List<ModelListItem>();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                foreach (var l in _agent.LlmsSnapshot)
                {
                    if (
                        l.ProviderName == "unconfigured"
                        || string.IsNullOrEmpty(l.ApiKey)
                        || l.ApiKey == "sk-test"
                    )
                        continue;
                    try
                    {
                        var pc = new PicoNode.AI.ProviderConfig
                        {
                            Name = l.ProviderName,
                            ApiKey = l.ApiKey,
                            BaseUrl = l.BaseUrl,
                            ApiFormat = l.ApiFormat,
                        };
                        var models = await PicoNode.AI.ModelDiscovery.DiscoverAsync(
                            http,
                            pc,
                            ct,
                            _logger
                        );
                        foreach (var m in models)
                            all.Add(new ModelListItem { Id = m.Id, OwnedBy = m.OwnedBy });
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warning(
                            $"Failed to discover models for '{l.ProviderName}': {ex.Message}"
                        );
                    }
                }
                return new HttpResponse
                {
                    StatusCode = 200,
                    Body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all)),
                    Headers = [new("Content-Type", "application/json; charset=utf-8")],
                };
            }
        );

        // ── NEW: Session endpoints ──

        app.MapGet(
            $"{p}/sessions",
            (_, _) =>
            {
                var home = HomeDir.Resolve();
                var json = SessionLister.ListJson(Path.Combine(home, "sessions"));
                return V(JsonHelper.RawJson(json));
            }
        );

        app.MapPost(
            $"{p}/sessions",
            async (ctx, ct) =>
            {
                try
                {
                    using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                    var body = await r.ReadToEndAsync(ct);
                    var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(body));
                    var name = doc.RootElement.TryGetProperty("name", out var nm)
                        ? nm.GetStringOrNull()
                        : null;
                    var participants = new List<Participant>
                    {
                        new Participant
                        {
                            AgentId = _agent.Id,
                            Name = AgentName,
                            JoinedAt = DateTime.UtcNow,
                        },
                    };
                    var session = await _sessionSystem.CreateAsync<SessionActor>(
                        new StartSession(name ?? "New chat", participants)
                    );
                    _logger?.Info($"Session created: {session.Id} name={name}");
                    return JsonHelper.JsonResponse(
                        new CreateSessionResponse { Id = session.Id, Name = name ?? "New chat" },
                        201
                    );
                }
                catch (Exception ex)
                {
                    _logger?.Error($"Create session failed: {ex}");
                    return JsonHelper.Error(500, $"Create session failed: {ex.Message}");
                }
            }
        );

        app.MapDelete(
            $"{p}/sessions/{{id}}",
            async (ctx, _) =>
            {
                var id = ctx.RouteValues["id"]?.ToString();
                if (id is null || !Guid.TryParse(id, out var guid))
                    return JsonHelper.Error(400, "invalid id");
                return await DeleteSessionAsync(guid);
            }
        );

        app.MapGet(
            $"{p}/sessions/{{id}}/messages",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"]?.ToString();
                if (id is null || !Guid.TryParse(id, out var guid))
                    return JsonHelper.Error(400, "invalid id");
                var session = await _sessionSystem.GetAsync<SessionActor>(guid);
                if (session is null)
                    return JsonHelper.Error(404, "session not found");
                var sctx = await _sessionSystem.AskAsync<SessionContext>(
                    guid,
                    new GetContextQuery()
                );
                var json = SessionMessageSerializer.ToJson(sctx.Messages.ToArray());
                return JsonHelper.RawJson(json);
            }
        );

        app.MapPost(
            $"{p}/sessions/{{id}}/message",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"]?.ToString();
                if (id is null || !Guid.TryParse(id, out var sessionGuid))
                    return JsonHelper.Error(400, "invalid id");
                using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var message = await reader.ReadToEndAsync(ct);
                if (string.IsNullOrWhiteSpace(message))
                    return JsonHelper.Error(400, "empty message");

                var context = await _sessionSystem.AskAsync<SessionContext>(
                    sessionGuid,
                    new GetContextQuery()
                );
                var pipe = new Pipe();
                var outputChannel = Channel.CreateUnbounded<ActorOutputEvent>();

                var turnLease = await _turnLock.AcquireAsync(sessionGuid, ct);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        _agentSystem.Send(
                            _runtime.Id,
                            new RunTurnCmd(message, sessionGuid, context, outputChannel.Writer)
                        );
                        await foreach (var evt in outputChannel.Reader.ReadAllAsync(ct))
                        {
                            var frame = ServerSse.BuildFrame(evt);
                            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        await pipe.Writer.CompleteAsync();
                        turnLease.Dispose();
                    }
                }); // no cancellation token passed — ensures finally always runs

                return new HttpResponse
                {
                    StatusCode = 200,
                    Headers =
                    [
                        new("Content-Type", "text/event-stream"),
                        new("Cache-Control", "no-cache"),
                        new("Connection", "close"),
                    ],
                    BodyStream = pipe.Reader.AsStream(),
                };
            }
        );

        app.MapPut(
            $"{p}/sessions/{{id}}/name",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"]?.ToString();
                if (id is null || !Guid.TryParse(id, out var guid))
                    return JsonHelper.Error(400, "invalid id");
                using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var body = await r.ReadToEndAsync(ct);
                var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(body));
                var name = doc.RootElement.TryGetProperty("name", out var nm)
                    ? nm.GetStringOrNull()
                    : null;
                if (name is null)
                    return JsonHelper.Error(400, "name required");
                _sessionSystem.Send(guid, new RenameSession(name));
                return JsonHelper.Ok();
            }
        );

        app.MapPost(
            $"{p}/sessions/{{id}}/retry",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"]?.ToString();
                if (id is null || !Guid.TryParse(id, out var guid))
                    return JsonHelper.Error(400, "invalid id");
                var entries = await _sessionSystem.AskAsync<SessionTreeEntryBase[]>(
                    guid,
                    new GetEntriesQuery()
                );
                var sctx = await _sessionSystem.AskAsync<SessionContext>(
                    guid,
                    new GetContextQuery()
                );
                if (sctx.LeafId is null)
                    return JsonHelper.Error(400, "no messages to retry");
                var leaf = entries.FirstOrDefault(e => e.Id == sctx.LeafId);
                if (leaf?.ParentId is not null)
                    _sessionSystem.Send(guid, new MoveLeaf(leaf.ParentId));
                return JsonHelper.Ok();
            }
        );

        // ── Agent CRUD ──

        app.MapGet(
            $"{p}/agents",
            (_, _) =>
                V(JsonHelper.RawJson($"[{{\"id\":\"{_agent.Id}\",\"name\":\"{AgentName}\"}}]"))
        );
        app.MapGet(
            $"{p}/agents/{{id}}",
            async (ctx, _) =>
            {
                var id = ctx.RouteValues["id"]?.ToString();
                if (id is null || !Guid.TryParse(id, out var guid))
                    return JsonHelper.Error(400, "invalid id");
                var config = await _agentSystem.AskAsync<AgentConfigSnapshot>(
                    guid,
                    new GetConfigQuery()
                );
                return JsonHelper.JsonResponse(config);
            }
        );
        app.MapPut(
            $"{p}/agents/{{id}}",
            async (ctx, ct) =>
            {
                var id = ctx.RouteValues["id"]?.ToString();
                if (id is null || !Guid.TryParse(id, out var guid))
                    return JsonHelper.Error(400, "invalid id");
                using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var body = await r.ReadToEndAsync(ct);
                var config = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(
                    Encoding.UTF8.GetBytes(body)
                );
                if (config is null)
                    return JsonHelper.Error(400, "invalid config");
                ConfigApplier.Apply(_agent, _agentSystem, config);
                _configSnapshotCached = null; // invalidate cache on config save
                return JsonHelper.Ok();
            }
        );

        // ── Config endpoints (unchanged logic) ──

        app.MapGet(
            $"{p}/config/status",
            (_, _) =>
            {
                var hasRealProvider = _agent.LlmsSnapshot.Any(l =>
                    l.ProviderName != "unconfigured"
                    && !string.IsNullOrEmpty(l.ApiKey)
                    && l.ApiKey != "sk-test"
                );
                return V(
                    JsonHelper.JsonResponse(
                        new ConfigStatusResponse
                        {
                            Configured = hasRealProvider,
                            Model = _agent.CurrentLlm.ModelId,
                            Provider = _agent.CurrentLlm.ProviderName,
                            Providers = _agent.LlmsSnapshot.Select(l => l.ProviderName).ToList(),
                            ThinkingEnabled = _agent.CurrentLlm.ThinkingEnabled,
                            ThinkingLevel = _agent
                                .CurrentLlm.ThinkingLevel.ToString()
                                .ToLowerInvariant(),
                            MaxTokens = _agent.CurrentLlm.MaxTokens,
                        }
                    )
                );
            }
        );

        app.MapGet(
            $"{p}/config/providers",
            (_, _) =>
                V(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        Body = Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(DefaultProviderTemplates)
                        ),
                        Headers = [new("Content-Type", "application/json; charset=utf-8")],
                    }
                )
        );

        app.MapPost(
            $"{p}/config/validate",
            async (ctx, ct) =>
            {
                using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var body = await r.ReadToEndAsync(ct);
                var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(body));
                var name =
                    doc.RootElement.TryGetProperty("provider", out var pv) ? pv.GetStringOrNull()
                    : doc.RootElement.TryGetProperty("name", out var nm) ? nm.GetStringOrNull()
                    : null;
                var key = doc.RootElement.TryGetProperty("apiKey", out var ak)
                    ? ak.GetStringOrNull()
                    : null;
                var url = doc.RootElement.TryGetProperty("baseUrl", out var bu)
                    ? bu.GetStringOrNull()
                    : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key))
                    return JsonHelper.Error(400, "provider and apiKey required");
                try
                {
                    var pc = new PicoNode.AI.ProviderConfig
                    {
                        Name = name,
                        ApiKey = key,
                        BaseUrl = url ?? "https://api.openai.com/v1",
                        ApiFormat = AiApiFormat.OpenAIChatCompletions,
                    };
                    using var http = new HttpClient();
                    var models = await PicoNode.AI.ModelDiscovery.DiscoverAsync(
                        http,
                        pc,
                        ct,
                        _logger
                    );
                    if (models.Length == 0)
                        return JsonHelper.Error(400, "No models found.");
                    return new HttpResponse
                    {
                        StatusCode = 200,
                        Body = Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(
                                models
                                    .Select(m => new ModelListItem
                                    {
                                        Id = m.Id,
                                        OwnedBy = m.OwnedBy,
                                    })
                                    .ToList()
                            )
                        ),
                        Headers = [new("Content-Type", "application/json; charset=utf-8")],
                    };
                }
                catch (Exception ex)
                {
                    _logger?.Error($"Config validation failed: {ex.Message}");
                    return JsonHelper.Error(400, ex.Message);
                }
            }
        );

        app.MapPost(
            $"{p}/config",
            async (ctx, ct) =>
            {
                using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var body = await r.ReadToEndAsync(ct);
                if (string.IsNullOrWhiteSpace(body))
                    return JsonHelper.Error(400, "empty body");
                AgentConfig? newConfig;
                try
                {
                    newConfig = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(
                        Encoding.UTF8.GetBytes(body)
                    );
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"Config save failed: {ex.Message}");
                    return JsonHelper.Error(400, "invalid json");
                }
                if (newConfig is null)
                    return JsonHelper.Error(400, "invalid config");
                ConfigApplier.Apply(_agent, _agentSystem, newConfig);
                _configSnapshotCached = null; // invalidate on config save
                if (_settingsPath is { Length: > 0 })
                {
                    var dir = Path.GetDirectoryName(_settingsPath);
                    if (dir is not null)
                        Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(_settingsPath, body, ct);
                }
                return JsonHelper.Ok();
            }
        );

        app.MapGet(
            $"{p}/system-prompt",
            async (_, _) =>
            {
                var prompt =
                    sp
                    ?? PicoNode.Agent.Domain.SystemPromptBuilder.Build(
                        _agent.ToolsSnapshot.ToArray(),
                        GetConfigSnapshot().Skills
                    );
                return JsonHelper.JsonResponse(new PromptResponse { Prompt = prompt });
            }
        );
        app.MapPost(
            $"{p}/system-prompt",
            async (ctx, _) =>
            {
                using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var body = await r.ReadToEndAsync();
                var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(body));
                sp = doc.RootElement.TryGetProperty("prompt", out var pp)
                    ? pp.GetStringOrNull()
                    : null;
                return JsonHelper.Ok();
            }
        );

        app.MapPost($"{p}/reload", (_, _) => V(JsonHelper.Ok()));
        app.MapPost(
            $"{p}/thinking",
            (ctx, _) =>
            {
                using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var body = r.ReadToEnd();
                var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(body));
                var lvl = doc.RootElement.TryGetProperty("level", out var lp)
                    ? lp.GetStringOrNull()
                    : null;
                if (lvl is { Length: > 0 })
                    _agentSystem.Send(_agent.Id, new SetThinkingLevelCmd(lvl));
                _configSnapshotCached = null; // invalidate: thinking level is in config snapshot
                return V(JsonHelper.Ok());
            }
        );

        app.MapPost($"{p}/model/switch", (_, _) => V(JsonHelper.Ok()));
        app.MapPost($"{p}/provider/switch", (_, _) => V(JsonHelper.Ok()));
    }

    // ── Private helpers ─────────────────────────────────────────────

    internal async Task<HttpResponse> DeleteSessionAsync(Guid sessionId)
    {
        try
        {
            _sessionSystem.Send(sessionId, new DeleteSession());
        }
        catch (KeyNotFoundException)
        {
            // Idempotent: session already deleted or never existed
        }

        try
        {
            await _sessionSystem.StopAsync(sessionId);
            _turnLock.Remove(sessionId);
        }
        catch (KeyNotFoundException)
        {
            // Already stopped
        }

        try
        {
            var home = HomeDir.Resolve();
            var file = Path.Combine(home, "sessions", $"{sessionId}.jsonl");
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Failed to delete session file for {sessionId}: {ex.Message}");
        }

        return JsonHelper.Ok();
    }

    private WebApp BuildWebApp()
    {
        var app = new WebApp(new SvcContainer(), new WebAppOptions());
        AddEndpoints(app, "/api");
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
            app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);
        return app;
    }

    private static ValueTask<HttpResponse> V(HttpResponse r) => ValueTask.FromResult(r);

    private static IPEndPoint ParseEndpoint(string uri)
    {
        var u = new Uri(uri);
        return new IPEndPoint(IPAddress.Loopback, u.IsDefaultPort || u.Port < 0 ? 80 : u.Port);
    }

    private static readonly List<ProviderTemplate> DefaultProviderTemplates =
    [
        new()
        {
            Name = "openai",
            Label = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            ApiFormat = "openai",
        },
        new()
        {
            Name = "anthropic",
            Label = "Anthropic",
            BaseUrl = "https://api.anthropic.com",
            ApiFormat = "anthropic",
        },
        new()
        {
            Name = "deepseek",
            Label = "DeepSeek",
            BaseUrl = "https://api.deepseek.com/v1",
            ApiFormat = "openai",
        },
        new()
        {
            Name = "groq",
            Label = "Groq",
            BaseUrl = "https://api.groq.com/openai/v1",
            ApiFormat = "openai",
        },
        new()
        {
            Name = "ollama",
            Label = "Ollama",
            BaseUrl = "http://localhost:11434/v1",
            ApiFormat = "openai",
        },
    ];

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_webServer is not null)
            await _webServer.DisposeAsync();
        if (_loggerFactory is not null)
            await _loggerFactory.DisposeAsync();
    }
}
