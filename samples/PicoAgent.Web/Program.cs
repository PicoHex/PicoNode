var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

var settingsPath = Path.Combine(homeDir, "settings.json");
if (!File.Exists(settingsPath))
{
    await File.WriteAllTextAsync(settingsPath,
        "{\"providers\":{},\"model\":\"\",\"thinkingEnabled\":true,\"thinkingLevel\":\"xhigh\",\"maxTokens\":4096}");
}

var config = new AgentConfig
{
    Providers = new Dictionary<string, ProviderEntry> { ["test"] = new() { ApiKey = "sk-test" } },
    Model = "test-model",
};
var factory = new AgentFactory().WithBuiltInTools();
var agent = factory.Build(config, homeDir);
agent.Start();
var adapter = new LlmClientAdapter(new SimpleLlmClient());

// Single WebApp: API endpoints + static files
var container = new SvcContainer();
container.Build();
var app = new WebApp(container, new WebAppOptions { ServerHeader = "PicoAgent.Web" });

// API endpoints
app.MapGet("/api/health", (_, _) => ValueTask.FromResult(Json("{\"status\":\"ok\"}")));
app.MapGet("/api/config/status", (_, _) =>
    ValueTask.FromResult(Json($"{{\"configured\":true,\"model\":\"{agent.CurrentLlm.ModelId}\",\"provider\":\"{agent.CurrentLlm.ProviderName}\",\"thinkingEnabled\":true,\"thinkingLevel\":\"xhigh\"}}")));
app.MapGet("/api/config/providers", (_, _) =>
    ValueTask.FromResult(Json("[{\"name\":\"test\",\"label\":\"Test\",\"baseUrl\":\"\",\"apiFormat\":\"openai\"}]")));
app.MapPost("/api/reload", (_, _) => ValueTask.FromResult(Json("{\"status\":\"ok\"}")));
app.MapPost("/api/thinking", (_, _) => ValueTask.FromResult(Json("{\"status\":\"ok\"}")));
app.MapPost("/api/model/switch", (_, _) => ValueTask.FromResult(Json("{\"status\":\"ok\"}")));
app.MapPost("/api/provider/switch", (_, _) => ValueTask.FromResult(Json("{\"status\":\"ok\"}")));
app.MapGet("/api/sessions", (_, _) => ValueTask.FromResult(Json("[\"default\"]")));
app.MapPost("/api/session/create/{id}", (_, _) => ValueTask.FromResult(Json("{\"status\":\"ok\"}")));
app.MapPost("/api/session/delete/{id}", (_, _) => ValueTask.FromResult(Json("{\"status\":\"ok\"}")));
app.MapPost("/api/session/save/{id}", (_, _) => ValueTask.FromResult(Json("{\"status\":\"ok\"}")));
app.MapGet("/api/session/{id}/messages", (_, _) => ValueTask.FromResult(Json("[]")));
app.MapPost("/api/config/validate", (_, _) => ValueTask.FromResult(Json("[]")));
app.MapPost("/api/config", (_, _) => ValueTask.FromResult(Json("{\"status\":\"saved\"}")));

// SSE message endpoint
app.MapPost("/api/session/{id}/message", async (ctx, ct) =>
{
    using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8, leaveOpen: true);
    var message = await reader.ReadToEndAsync(ct);
    if (string.IsNullOrWhiteSpace(message)) return JsonErr(400, "empty");
    var pipe = new Pipe();
    _ = Task.Run(async () =>
    {
        try
        {
            await agent.RunTurn(message, adapter, factory.GetToolRunner(), ct,
                async (kind, text) =>
                {
                    var sse = $"event: {kind}\ndata: {text ?? ""}\n\n";
                    await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(sse), ct);
                });
            await pipe.Writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"event: error\ndata: {ex.Message}\n\n"), ct);
        }
        finally { await pipe.Writer.CompleteAsync(); }
    }, ct);
    return new HttpResponse
    {
        StatusCode = 200,
        Headers = [new("Content-Type", "text/event-stream"), new("Cache-Control", "no-cache")],
        BodyStream = pipe.Reader.AsStream(),
    };
});

// Static files (registered last = highest priority in WebApp)
var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot))
    app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

// Start single server on port 9000
var ws = new WebServer(app, new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000) });
await ws.StartAsync();
Console.WriteLine("PicoAgent.Web on http://localhost:9000");
await Task.Delay(-1);

static HttpResponse Json(string body) => new()
{
    StatusCode = 200, Body = Encoding.UTF8.GetBytes(body),
    Headers = [new("Content-Type", "application/json; charset=utf-8")],
};
static HttpResponse JsonErr(int code, string msg) => new()
{
    StatusCode = code, Body = Encoding.UTF8.GetBytes($"{{\"error\":\"{msg}\"}}"),
    Headers = [new("Content-Type", "application/json; charset=utf-8")],
};

internal sealed class SimpleLlmClient : PicoNode.AI.ILLmClient
{
    public async IAsyncEnumerable<PicoNode.AI.AssistantMessageEvent> StreamAsync(
        PicoNode.AI.Model model, PicoNode.AI.Types.ChatContext ctx,
        PicoNode.AI.StreamOptions? opts, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new PicoNode.AI.AssistantMessageEvent.Done
        {
            Message = new PicoNode.AI.Message
            {
                Role = "assistant",
                ContentBlocks = [new PicoNode.AI.ContentBlock { Type = "text", Text = "Hello from PicoAgent!" }],
                StopReason = "end_turn",
            },
        };
    }
}
