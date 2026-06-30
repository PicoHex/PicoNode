using System.Text.Json;
using PicoNode.AI;

var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

var settingsPath = Path.Combine(homeDir, "settings.json");
if (!File.Exists(settingsPath))
{
    // Bootstrap: create minimal config so Agent can start and user can configure via Web UI
    await ConfigLoader.SaveAsync(
        settingsPath,
        new AgentConfig
        {
            Providers = [],
            Model = "",
            ThinkingLevel = "medium",
            ThinkingEnabled = true,
            MaxTokens = 4096,
        }
    );
    Console.Error.WriteLine($"Created bootstrap config at {settingsPath}. Configure via web UI.");
}

var config = await ConfigLoader.LoadAsync(settingsPath);
if (config is null)
{
    await Console.Error.WriteLineAsync($"Failed to load settings from: {settingsPath}");
    return;
}
var validation = ConfigLoader.Validate(config);
if (!validation.IsValid)
{
    foreach (var err in validation.Errors)
        await Console.Error.WriteLineAsync(err);
    return;
}

var logger = new LoggerFactory([new ConsoleSink(new ConsoleFormatter())]).CreateLogger("Web");

// ── 启动 Agent 后端 ──
await using var agent = await Agent.CreateAsync(config, homeDir, logger);
await agent.ListenAsync("http://localhost:19998");
var agentPort = 19998;
await using var client = new AgentHttpClient($"http://localhost:{agentPort}");

// ── 构建前端 WebApp ──
var container = new SvcContainer();
container.Build();
var app = new WebApp(container, new WebAppOptions { ServerHeader = "PicoAgent.Web" });

// ── API 路由 ──

// SSE 端点
app.MapPost(
    "/api/session/{id}/message",
    async (ctx, ct) =>
    {
        var id = ctx.RouteValues["id"] ?? "default";
        using var reader = new StreamReader(
            ctx.Request.BodyStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true
        );
        var body = await reader.ReadToEndAsync(ct);

        var pipe = new Pipe();
        var sse = new SseConnection(pipe.Writer);
        _ = ForwardSseAsync(sse, pipe.Writer, client, id, body, ct);

        return new HttpResponse
        {
            StatusCode = 200,
            Headers = [new("Content-Type", "text/event-stream"), new("Cache-Control", "no-cache")],
            BodyStream = pipe.Reader.AsStream(),
        };
    }
);

// 非 SSE 端点
app.MapGet("/api/health", async (_, ct) => OkJson(await client.GetHealthAsync(ct)));
app.MapGet(
    "/api/models",
    async (_, ct) =>
    {
        var list = await client.ListModelsAsync(null, ct);
        var json =
            "["
            + string.Join(
                ",",
                list.Select(m =>
                    $"{{\"id\":\"{EscapeJson(m.Id)}\",\"ownedBy\":\"{EscapeJson(m.OwnedBy)}\"}}"
                )
            )
            + "]";
        return OkJson(json);
    }
);
app.MapGet(
    "/api/sessions",
    async (_, ct) =>
    {
        var list = await client.ListSessionsAsync(ct);
        var json = "[" + string.Join(",", list.Select(s => $"\"{EscapeJson(s)}\"")) + "]";
        return OkJson(json);
    }
);
app.MapGet(
    "/api/session/{id}/messages",
    async (ctx, ct) =>
    {
        var id = ctx.RouteValues["id"] ?? "default";
        var msgs = await client.GetMessagesAsync(id, ct);
        var json =
            "[" + string.Join(",", msgs.Select(m => PicoJetson.JsonSerializer.Serialize(m))) + "]";
        return OkJson(json);
    }
);

app.MapPost(
    "/api/model/switch",
    async (ctx, ct) =>
    {
        var json = await ReadJsonAsync(ctx, ct);
        var modelId = json?.TryGetProperty("modelId", out var mid) == true ? mid.GetString() : null;
        if (string.IsNullOrWhiteSpace(modelId))
            return ErrorJson(400, "modelId required");
        return (await client.SwitchModelAsync(modelId, ct))
            ? Ok()
            : ErrorJson(400, "Switch failed");
    }
);

app.MapPost(
    "/api/provider/switch",
    async (ctx, ct) =>
    {
        var json = await ReadJsonAsync(ctx, ct);
        var provider = json?.TryGetProperty("provider", out var pv) == true ? pv.GetString() : null;
        if (string.IsNullOrWhiteSpace(provider))
            return ErrorJson(400, "provider required");
        return (await client.SwitchProviderAsync(provider, ct))
            ? Ok()
            : ErrorJson(400, "Switch failed");
    }
);

app.MapPost(
    "/api/thinking",
    async (ctx, ct) =>
    {
        var json = await ReadJsonAsync(ctx, ct);
        var enabled = json?.TryGetProperty("enabled", out var en) == true ? en.GetBoolean() : true;
        var level =
            json?.TryGetProperty("level", out var lv) == true
                ? (lv.GetString() ?? "medium")
                : "medium";
        return (await client.SwitchThinkingAsync(enabled, level, ct))
            ? Ok()
            : ErrorJson(400, "Switch failed");
    }
);

app.MapPost(
    "/api/session/create/{id}",
    async (ctx, ct) =>
    {
        var id = ctx.RouteValues["id"] ?? "";
        return (await client.CreateSessionAsync(id, ct)) ? Ok() : ErrorJson(400, "Create failed");
    }
);

app.MapPost(
    "/api/session/delete/{id}",
    async (ctx, ct) =>
    {
        var id = ctx.RouteValues["id"] ?? "";
        return (await client.DeleteSessionAsync(id, ct)) ? Ok() : ErrorJson(404, "Not found");
    }
);

app.MapPost(
    "/api/session/save/{id}",
    async (ctx, ct) =>
    {
        var id = ctx.RouteValues["id"] ?? "default";
        return (await client.SaveSessionAsync(id, ct)) ? Ok() : ErrorJson(400, "Save failed");
    }
);

app.MapPost(
    "/api/reload",
    async (_, ct) =>
    {
        return (await client.ReloadAsync(ct)) ? Ok() : ErrorJson(400, "Reload failed");
    }
);

// ── Config proxy (forward to Agent backend) ──

app.MapGet(
    "/api/config/status",
    async (_, ct) =>
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{agentPort}") };
        var json = await http.GetStringAsync("/config/status", ct);
        return OkJson(json);
    }
);

app.MapGet(
    "/api/config/providers",
    async (_, ct) =>
    {
        // Proxy to Agent backend
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{agentPort}") };
        var json = await http.GetStringAsync("/config/providers", ct);
        return OkJson(json);
    }
);

app.MapPost(
    "/api/config/validate",
    async (ctx, ct) =>
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{agentPort}") };
        using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        var resp = await http.PostAsync(
            "/config/validate",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct
        );
        var result = await resp.Content.ReadAsStringAsync(ct);
        return new HttpResponse
        {
            StatusCode = (int)resp.StatusCode,
            Body = Encoding.UTF8.GetBytes(result),
            Headers = [new("Content-Type", "application/json")],
        };
    }
);

app.MapPost(
    "/api/config",
    async (ctx, ct) =>
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{agentPort}") };
        using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        var resp = await http.PostAsync(
            "/config",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct
        );
        var result = await resp.Content.ReadAsStringAsync(ct);
        return new HttpResponse
        {
            StatusCode = (int)resp.StatusCode,
            Body = Encoding.UTF8.GetBytes(result),
            Headers = [new("Content-Type", "application/json")],
        };
    }
);

// ── 静态文件 (最后注册 = 最先执行) ──
var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot))
    app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

// ── 启动 ──
var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000), Logger = logger }
);
await server.StartAsync();
logger.Info("PicoAgent.Web serving on http://localhost:9000");

await WaitForShutdownAsync();
await agent.StopAsync();

// ── Helpers ──

static async Task ForwardSseAsync(
    SseConnection sse,
    PipeWriter writer,
    AgentHttpClient client,
    string sessionId,
    string body,
    CancellationToken ct
)
{
    try
    {
        await foreach (var evt in client.SendMessageAsync(sessionId, body, ct))
        {
            switch (evt)
            {
                case AssistantMessageEvent.TextDelta td:
                    await sse.WriteJsonAsync(
                        PicoJetson.JsonSerializer.Serialize(
                            new SseDeltaEvent { content = td.Delta }
                        ),
                        ct
                    );
                    break;
                case AssistantMessageEvent.ThinkingDelta th:
                    await sse.WriteJsonAsync(
                        PicoJetson.JsonSerializer.Serialize(
                            new SseThinkingEvent { content = th.Delta }
                        ),
                        ct
                    );
                    break;
                case AssistantMessageEvent.ToolCallStart ts:
                    {
                        var b = ts.Partial.ContentBlocks?.ElementAtOrDefault(ts.Index);
                        await sse.WriteJsonAsync(
                            PicoJetson.JsonSerializer.Serialize(
                                new SseToolCallStartEvent
                                {
                                    toolCallId = b?.Id ?? "",
                                    toolName = b?.Name ?? "",
                                }
                            ),
                            ct
                        );
                    }
                    break;
                case AssistantMessageEvent.ToolCallDelta td:
                    {
                        var b = td.Partial.ContentBlocks?.ElementAtOrDefault(td.Index);
                        await sse.WriteJsonAsync(
                            PicoJetson.JsonSerializer.Serialize(
                                new SseToolCallDeltaEvent
                                {
                                    toolCallId = b?.Id ?? "",
                                    content = td.Delta,
                                }
                            ),
                            ct
                        );
                    }
                    break;
                case AssistantMessageEvent.ToolCallEnd te:
                    await sse.WriteJsonAsync(
                        PicoJetson.JsonSerializer.Serialize(
                            new SseToolCallEndEvent { toolCallId = te.Call.Id ?? "" }
                        ),
                        ct
                    );
                    break;
                case AssistantMessageEvent.Done d:
                    await sse.WriteJsonAsync(
                        PicoJetson.JsonSerializer.Serialize(
                            new SseDoneEvent { stopReason = d.Message.StopReason ?? "" }
                        ),
                        ct
                    );
                    break;
                case AssistantMessageEvent.Error e:
                    await sse.WriteJsonAsync(
                        PicoJetson.JsonSerializer.Serialize(
                            new SseErrorEvent { message = e.Message.ErrorMessage ?? "Agent error" }
                        ),
                        ct
                    );
                    break;
            }
        }
        await sse.CompleteAsync(ct);
    }
    catch (OperationCanceledException)
    {
        await writer.CompleteAsync();
    }
    catch (Exception ex)
    {
        await writer.CompleteAsync(ex);
    }
}

static async Task<JsonElement?> ReadJsonAsync(WebContext ctx, CancellationToken ct)
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

static HttpResponse Ok() => WebResults.Json(200, "{\"status\":\"ok\"}");
static HttpResponse OkJson(string json) => WebResults.Json(200, json);
static HttpResponse ErrorJson(int code, string message) =>
    WebResults.Json(code, $"{{\"error\":\"{EscapeJson(message)}\"}}");

static string EscapeJson(string s)
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

static async Task WaitForShutdownAsync()
{
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        tcs.TrySetResult();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
    try
    {
        await tcs.Task;
    }
    catch (OperationCanceledException) { }
}
