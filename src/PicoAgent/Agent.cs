// src/PicoAgent/Agent.cs
namespace PicoAgent;

using System.Net;

public sealed partial class Agent : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly AgentHost _host;
    private readonly CapabilityRegistry _registry;
    private readonly string? _homeDir;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyDictionary<string, ProviderConfig> _providerConfigs;
    private readonly IReadOnlyDictionary<string, ICircuitBreaker> _breakers;
    private readonly IReadOnlyDictionary<string, ILLmClient> _clients;
    private readonly ILogger? _logger;
    private Model _pendingModel;
    private WebServer? _server;
    private bool _disposed;

    private Agent(
        AgentHost host,
        CapabilityRegistry registry,
        string? homeDir,
        HttpClient http,
        bool ownsHttpClient,
        IReadOnlyDictionary<string, ProviderConfig> providerConfigs,
        IReadOnlyDictionary<string, ICircuitBreaker> breakers,
        IReadOnlyDictionary<string, ILLmClient> clients,
        Model initialModel,
        ILogger? logger = null
    )
    {
        _host = host;
        _registry = registry;
        _homeDir = homeDir;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _providerConfigs = providerConfigs;
        _breakers = breakers;
        _clients = clients;
        _pendingModel = initialModel;
        _logger = logger;
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
        var host = await builder.BuildAgentHostInternalAsync();
        var registry = builder.GetRegistry();
        var http = builder.GetHttpClient();
        var ownsHttp = builder.GetHttpClientIsOwned();
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
            providerConfigs,
            breakers,
            clients,
            model,
            logger
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
        IReadOnlyDictionary<string, ILLmClient>? clients = null)
    {
        http ??= new HttpClient();
        providerConfigs ??= new Dictionary<string, ProviderConfig>();
        breakers ??= new Dictionary<string, ICircuitBreaker>();
        clients ??= new Dictionary<string, ILLmClient>();
        return new Agent(host, registry, null, http, ownsHttpClient: true,
            providerConfigs, breakers, clients, initialModel);
    }

    // ── Private ──

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
                var json = await ReadJsonBodyAsync(ctx, ct);
                var modelId =
                    json?.TryGetProperty("modelId", out var mid) == true ? mid.GetString() : null;
                if (string.IsNullOrWhiteSpace(modelId))
                    return JsonError(400, "INVALID_ARGUMENT", "modelId is required");
                SwitchModel(modelId);
                return JsonOk();
            }
        );

        // POST /provider/switch (async, reads body)
        app.MapPost(
            "/provider/switch",
            async (ctx, ct) =>
            {
                var json = await ReadJsonBodyAsync(ctx, ct);
                var provider =
                    json?.TryGetProperty("provider", out var pv) == true ? pv.GetString() : null;
                if (string.IsNullOrWhiteSpace(provider))
                    return JsonError(400, "INVALID_ARGUMENT", "provider is required");
                return SwitchProvider(provider)
                    ? JsonOk()
                    : JsonError(404, "NOT_FOUND", "Provider not configured");
            }
        );

        // POST /thinking (async, reads body)
        app.MapPost(
            "/thinking",
            async (ctx, ct) =>
            {
                var json = await ReadJsonBodyAsync(ctx, ct);
                var enabled =
                    json?.TryGetProperty("enabled", out var en) == true ? en.GetBoolean() : true;
                var levelStr =
                    json?.TryGetProperty("level", out var lv) == true ? lv.GetString() : "medium";
                var level = AgentConfig.ParseLevel(levelStr);
                if (level is null)
                    return JsonError(400, "INVALID_ARGUMENT", $"Invalid level: {levelStr}");
                SwitchThinking(enabled, level.Value);
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
                            + string.Join(
                                ",",
                                msgs.Select(m => PicoJetson.JsonSerializer.Serialize(m))
                            )
                            + "]";
                        return JsonResponse(200, json);
                    });

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

        return app;
    }

    private static async Task<JsonElement?> ReadJsonBodyAsync(WebContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(
            ctx.Request.BodyStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true
        );
        var text = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
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
        if (u.Host is "localhost" or "127.0.0.1" or "::1")
            return new IPEndPoint(IPAddress.Loopback, u.Port > 0 ? u.Port : 80);
        if (IPAddress.TryParse(u.Host, out var addr))
            return new IPEndPoint(addr, u.Port > 0 ? u.Port : 80);
        return new IPEndPoint(IPAddress.Loopback, u.Port > 0 ? u.Port : 80);
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
