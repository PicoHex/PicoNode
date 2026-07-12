namespace PicoAgent;

/// <summary>
/// Helper for producing JSON HTTP responses via PicoJetson.
/// Replaces all hand-crafted JSON string interpolation.
/// </summary>
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
    private readonly ActorSystem _system;
    private readonly ILlmClient _llmClient;
    private readonly IToolRunner _toolRunner;
    private WebServer? _webServer;
    private bool _isListening;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    public int Port => _webServer?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;

    public Server(Agent agent, ActorSystem system, ILlmClient llmClient, IToolRunner toolRunner)
    {
        _agent = agent;
        _system = system;
        _llmClient = llmClient;
        _toolRunner = toolRunner;
    }

    public async Task ListenAsync(string uri)
    {
        if (_isListening)
            throw new InvalidOperationException("Already listening");
        _isListening = true;
        _system.Send(_agent.Id, new StartAgent());
        _webServer = new WebServer(
            BuildWebApp(),
            new WebServerOptions { Endpoint = ParseEndpoint(uri) }
        );
        await _webServer.StartAsync(CancellationToken.None);
    }

    // ── HTTP Endpoints ──────────────────────────────────────────────

    public static void AddEndpoints(
        WebApp app,
        Agent a,
        ActorSystem system,
        ILlmClient llm,
        IToolRunner tr,
        string prefix = "",
        string? settingsPath = null
    )
    {
        var turnLock = new SemaphoreSlim(1, 1);
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
                            Model = a.CurrentLlm.ModelId,
                            Provider = a.CurrentLlm.ProviderName,
                        }
                    )
                )
        );

        // Models
        app.MapGet(
            $"{p}/models",
            async (_, ct) =>
            {
                var all = new List<ModelListItem>();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                foreach (var l in a.LlmsSnapshot)
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
                        var models = await PicoNode.AI.ModelDiscovery.DiscoverAsync(http, pc, ct);
                        foreach (var m in models)
                            all.Add(new ModelListItem { Id = m.Id, OwnedBy = m.OwnedBy });
                    }
                    catch
                    { /* skip failed */
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

        // Sessions
        app.MapGet(
            $"{p}/sessions",
        app.MapGet($"{p}/sessions", (_, _) =>
            V(JsonHelper.RawJson("[\"default\"]"))
        );
        );

        // Config status
        app.MapGet(
            $"{p}/config/status",
            (_, _) =>
            {
                var hasRealProvider = a.LlmsSnapshot.Any(l =>
                    l.ProviderName != "unconfigured"
                    && !string.IsNullOrEmpty(l.ApiKey)
                    && l.ApiKey != "sk-test"
                );
                return V(
                    JsonHelper.JsonResponse(
                        new ConfigStatusResponse
                        {
                            Configured = hasRealProvider,
                            Model = a.CurrentLlm.ModelId,
                            Provider = a.CurrentLlm.ProviderName,
                            Providers = a.LlmsSnapshot.Select(l => l.ProviderName).ToList(),
                            ThinkingEnabled = a.CurrentLlm.ThinkingEnabled,
                            ThinkingLevel = a
                                .CurrentLlm.ThinkingLevel.ToString()
                                .ToLowerInvariant(),
                            MaxTokens = a.CurrentLlm.MaxTokens,
                        }
                    )
                );
            }
        );

        // Provider templates
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

        // Config validate
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
                    var models = await PicoNode.AI.ModelDiscovery.DiscoverAsync(http, pc, ct);
                    if (models.Length == 0)
                        return JsonHelper.Error(
                            400,
                            "No models found. Check your API key or base URL."
                        );
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
                    return JsonHelper.Error(400, ex.Message);
                }
            }
        );

        // Config save
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
                catch
                {
                    return JsonHelper.Error(400, "invalid json");
                }
                if (newConfig is null)
                    return JsonHelper.Error(400, "invalid config");

                var newProviderNames = newConfig.Providers.Keys.ToList();
                foreach (var (name, entry) in newConfig.Providers)
                {
                    var llm = new Llm
                    {
                        ProviderName = name,
                        ModelId = newConfig.Model ?? name,
                        ApiKey = entry.ApiKey,
                        BaseUrl = entry.BaseUrl ?? "",
                        ApiFormat = (entry.ApiFormat?.ToLowerInvariant()) switch
                        {
                            "anthropic" => AiApiFormat.AnthropicMessages,
                            _ => AiApiFormat.OpenAIChatCompletions,
                        },
                        ThinkingLevel =
                            AgentConfig.ParseLevel(newConfig.ThinkingLevel)
                            ?? AgentConfig.DefaultThinkingLevel,
                        MaxTokens = newConfig.MaxTokens ?? 4096,
                        ThinkingEnabled = newConfig.ThinkingEnabled,
                    };
                    var existing = a.LlmsSnapshot.FirstOrDefault(l => l.ProviderName == name);
                    if (existing is not null)
                        system.Send(a.Id, new RemoveLlmCmd(name, existing.ModelId));
                    system.Send(a.Id, new AddLlmCmd(llm));
                }
                var toRemove = a
                    .LlmsSnapshot.Where(l => !newProviderNames.Contains(l.ProviderName))
                    .ToList();
                var newCurrent =
                    newProviderNames.FirstOrDefault() ?? a.LlmsSnapshot[0].ProviderName;
                var newModel = newConfig.Model ?? newCurrent;
                if (toRemove.Any(r => r.ProviderName == a.CurrentLlm.ProviderName))
                    system.Send(a.Id, new SwitchLlmCmd(newCurrent, newModel));
                foreach (var old in toRemove)
                {
                    if (a.LlmsSnapshot.Count > 1 && old.ProviderName != a.CurrentLlm.ProviderName)
                        system.Send(a.Id, new RemoveLlmCmd(old.ProviderName, old.ModelId));
                }
                system.Send(a.Id, new SwitchLlmCmd(newCurrent, newModel));

                if (settingsPath is { Length: > 0 })
                {
                    var dir = Path.GetDirectoryName(settingsPath);
                    if (dir is not null)
                        Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(settingsPath, body, ct);
                }

                return JsonHelper.Ok();
            }
        );

        // System prompt
        app.MapGet(
            $"{p}/system-prompt",
            (_, _) =>
            {
                var prompt =
                    sp
                    ?? PicoNode.Agent.Domain.SystemPromptBuilder.Build(
                        a.ToolsSnapshot.ToArray(),
                        a.Skills
                    );
                return V(JsonHelper.JsonResponse(new PromptResponse { Prompt = prompt }));
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

        // Reload
        app.MapPost($"{p}/reload", (_, _) => V(JsonHelper.Ok()));

        // Thinking
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
                    system.Send(a.Id, new SetThinkingLevelCmd(lvl));
                return V(JsonHelper.Ok());
            }
        );

        // Model/Provider switch (stub placeholders)
        app.MapPost(
            $"{p}/model/switch",
            (ctx, _) =>
            {
                return V(JsonHelper.Ok());
            }
        );
        app.MapPost(
            $"{p}/provider/switch",
            (ctx, _) =>
            {
                return V(JsonHelper.Ok());
            }
        );

        // Session management
        app.MapPost($"{p}/session/create/{{id}}", (_, _) => V(JsonHelper.Ok()));
        app.MapPost($"{p}/session/delete/{{id}}", (_, _) => V(JsonHelper.Ok()));
        app.MapPost($"{p}/session/save/{{id}}", (_, _) => V(JsonHelper.Ok()));
        app.MapGet($"{p}/session/{{id}}/messages", (_, _) => V(JsonHelper.RawJson("[]")));
        app.MapPost($"{p}/session/{{id}}/retry", (_, _) => V(JsonHelper.Ok()));
        app.MapPost(
            $"{p}/session/{{id}}/compact",
            (_, _) =>
                V(
                    JsonHelper.JsonResponse(
                        new CompactResponse
                        {
                            CompressedCount = 0,
                            Summary = null,
                            TokensSaved = 0,
                        }
                    )
                )
        );

        // SSE message endpoint
        app.MapPost(
            $"{p}/session/{{id}}/message",
            async (ctx, ct) =>
            {
                using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var message = await reader.ReadToEndAsync(ct);
                if (string.IsNullOrWhiteSpace(message))
                    return JsonHelper.Error(400, "empty message");
                var pipe = new Pipe();
                _ = Task.Run(
                    async () =>
                    {
                        await turnLock.WaitAsync(ct);
                        try
                        {
                            var outputChannel = Channel.CreateUnbounded<ActorOutputEvent>();
                            a.OutputWriter = outputChannel.Writer;

                            system.Send(a.Id, new RunTurn(message));

                            await foreach (var evt in outputChannel.Reader.ReadAllAsync(ct))
                            {
                                var sseJson = BuildSseJson(evt);
                                await pipe.Writer.WriteAsync(
                                    Encoding.UTF8.GetBytes($"data: {sseJson}\n\n"),
                                    ct
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            var errJson = BuildSseJson(new ActorOutputEvent("error", ex.Message));
                            await pipe.Writer.WriteAsync(
                                Encoding.UTF8.GetBytes($"data: {errJson}\n\n"),
                                ct
                            );
                        }
                        finally
                        {
                            turnLock.Release();
                            await pipe.Writer.CompleteAsync();
                        }
                    },
                    ct
                );
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
    }

    // ── Private helpers ─────────────────────────────────────────────

    internal static string BuildSseJson(ActorOutputEvent evt)
    {
        var tp = evt.Type == "text" ? "delta" : evt.Type;
        if (tp == "done")
            return "{\"type\":\"done\"}";

        var sb = new StringBuilder(128);
        sb.Append("{\"type\":\"");
        sb.Append(tp);
        sb.Append("\"");

        sb.Append(",\"content\":\"");
        sb.Append(EscapeJsonString(evt.Data ?? string.Empty));
        sb.Append('"');

        if (evt.ToolCallId is { Length: > 0 })
        {
            sb.Append(",\"toolCallId\":\"");
            sb.Append(evt.ToolCallId);
            sb.Append('"');
        }

        if (evt.ToolName is { Length: > 0 })
        {
            sb.Append(",\"toolName\":\"");
            sb.Append(evt.ToolName);
            sb.Append('"');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
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
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private WebApp BuildWebApp()
    {
        var app = new WebApp(new SvcContainer(), new WebAppOptions());
        AddEndpoints(app, _agent, _system, _llmClient, _toolRunner, "/api");

        // Serve static files from wwwroot alongside the app directory
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
    }
}
