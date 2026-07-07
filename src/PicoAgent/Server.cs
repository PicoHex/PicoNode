namespace PicoAgent;

using DomainAgent = PicoNode.Agent.Domain.Agent;
using DomainInterfaces = PicoNode.Agent.Domain;

public sealed class Server : IAsyncDisposable
{
    private readonly DomainAgent _agent;
    private readonly DomainInterfaces.ILlmClient _llmClient;
    private readonly DomainInterfaces.IToolRunner _toolRunner;
    private WebServer? _webServer;
    private bool _isListening;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private string? _systemPrompt;

    public int Port => _webServer?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;

    public Server(DomainAgent agent, DomainInterfaces.ILlmClient llmClient, DomainInterfaces.IToolRunner toolRunner)
    { _agent = agent; _llmClient = llmClient; _toolRunner = toolRunner; }

    public async Task ListenAsync(string uri)
    {
        if (_isListening) throw new InvalidOperationException("Already listening");
        _isListening = true; _agent.Start();
        _webServer = new WebServer(BuildWebApp(), new WebServerOptions { Endpoint = ParseEndpoint(uri) });
        await _webServer.StartAsync(CancellationToken.None);
    }

    public static void AddEndpoints(WebApp app, DomainAgent a, DomainInterfaces.ILlmClient llm, DomainInterfaces.IToolRunner tr, string prefix = "")
    {
        var turnLock = new SemaphoreSlim(1, 1);
        string? sp = null;
        var p = prefix;

        app.MapGet($"{p}/health", (_, _) => V(Json($"{{\"status\":\"ok\",\"model\":\"{a.CurrentLlm.ModelId}\",\"provider\":\"{a.CurrentLlm.ProviderName}\"}}")));
        app.MapGet($"{p}/models", (_, _) => V(Json("[]")));
        app.MapGet($"{p}/sessions", (_, _) => V(Json("[\"default\"]")));
        app.MapGet($"{p}/config/status", (_, _) =>
        {
            var hasRealProvider = a.Llms.Any(l =>
                l.ProviderName != "unconfigured"
                && !string.IsNullOrEmpty(l.ApiKey)
                && l.ApiKey != "sk-test"
            );
            return V(Json(
                $"{{\"configured\":{hasRealProvider.ToString().ToLower()},\"model\":\"{a.CurrentLlm.ModelId}\",\"provider\":\"{a.CurrentLlm.ProviderName}\",\"providers\":[{string.Join(",", a.Llms.Select(l => $"\"{l.ProviderName}\""))}],\"thinkingEnabled\":{a.CurrentLlm.ThinkingEnabled.ToString().ToLower()},\"thinkingLevel\":\"{a.CurrentLlm.ThinkingLevel.ToString().ToLowerInvariant()}\",\"maxTokens\":{a.CurrentLlm.MaxTokens}}}"
            ));
        });
        app.MapGet($"{p}/config/providers", (_, _) => V(Json(ProviderTemplates)));
        app.MapPost($"{p}/config/validate", async (ctx, ct) =>
        {
            using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
            var body = await r.ReadToEndAsync(ct);
            var name = ExtractJsonString(body, "provider") ?? ExtractJsonString(body, "name");
            var key = ExtractJsonString(body, "apiKey");
            var url = ExtractJsonString(body, "baseUrl");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key))
                return Error(400, "provider and apiKey required");
            try
            {
                var pc = new PicoNode.AI.ProviderConfig { Name = name, ApiKey = key, BaseUrl = url ?? "https://api.openai.com/v1", ApiFormat = AiApiFormat.OpenAIChatCompletions };
                using var http = new HttpClient();
                var models = await PicoNode.AI.ModelDiscovery.DiscoverAsync(http, pc, ct);
                if (models.Length == 0) return Error(400, "No models found. Check your API key or base URL.");
                var json = "[" + string.Join(",", models.Select(m => $"{{\"id\":\"{EscapeJson(m.Id)}\",\"ownedBy\":\"{EscapeJson(m.OwnedBy)}\"}}")) + "]";
                return Json(json);
            }
            catch (Exception ex) { return Error(400, ex.Message); }
        });
        app.MapPost($"{p}/config", async (ctx, ct) =>
        {
            using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
            var body = await r.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return Error(400, "empty body");

            // Parse and validate
            AgentConfig? newConfig;
            try { newConfig = PicoJetson.JsonSerializer.Deserialize<AgentConfig>(Encoding.UTF8.GetBytes(body)); }
            catch { return Error(400, "invalid json"); }
            if (newConfig is null) return Error(400, "invalid config");

            // Add new providers first (can't remove all — invariant requires at least 1)
            var newProviderNames = newConfig.Providers.Keys.ToList();
            foreach (var (name, entry) in newConfig.Providers)
            {
                var llm = new Llm
                {
                    ProviderName = name, ModelId = newConfig.Model ?? name,
                    ApiKey = entry.ApiKey, BaseUrl = entry.BaseUrl ?? "",
                    ApiFormat = (entry.ApiFormat?.ToLowerInvariant()) switch { "anthropic" => AiApiFormat.AnthropicMessages, _ => AiApiFormat.OpenAIChatCompletions },
                    ThinkingLevel = AgentConfig.ParseLevel(newConfig.ThinkingLevel) ?? AgentConfig.DefaultThinkingLevel,
                    MaxTokens = newConfig.MaxTokens ?? 4096, ThinkingEnabled = newConfig.ThinkingEnabled,
                };
                var existing = a.Llms.FirstOrDefault(l => l.ProviderName == name);
                if (existing is null) a.AddLlm(llm);
            }
            // Remove old providers not in new config
            var toRemove = a.Llms.Where(l => !newProviderNames.Contains(l.ProviderName)).ToList();
            var newCurrent = newProviderNames.FirstOrDefault() ?? a.Llms[0].ProviderName;
            var newModel = newConfig.Model ?? newCurrent;
            if (toRemove.Any(r => r.ProviderName == a.CurrentLlm.ProviderName))
                a.SwitchLlm(newCurrent, newModel);
            foreach (var old in toRemove)
            {
                if (a.Llms.Count > 1 && old.ProviderName != a.CurrentLlm.ProviderName)
                    a.RemoveLlm(old.ProviderName, old.ModelId);
            }
            a.SwitchLlm(newCurrent, newModel);

            return Ok();
        });
        app.MapGet($"{p}/system-prompt", (_, _) => V(Json($"{{\"prompt\":\"{EscapeJson(sp ?? "")}\"}}")));
        app.MapPost($"{p}/system-prompt", async (ctx, _) => { using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8); var body = await r.ReadToEndAsync(); sp = ExtractJsonString(body, "prompt"); return Ok(); });
        app.MapPost($"{p}/reload", (_, _) => V(Ok()));
        app.MapPost($"{p}/thinking", (ctx, _) => { using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8); var body = r.ReadToEnd(); var lvl = ExtractJsonString(body, "level"); if (lvl is { Length: > 0 }) { a.CurrentLlm.ThinkingLevel = lvl.ToLower() switch { "minimal" => ThinkingLevel.Minimal, "low" => ThinkingLevel.Low, "medium" => ThinkingLevel.Medium, "high" => ThinkingLevel.High, "xhigh" => ThinkingLevel.XHigh, _ => a.CurrentLlm.ThinkingLevel }; } return V(Ok()); });
        app.MapPost($"{p}/model/switch", (ctx, _) => { using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8); var body = r.ReadToEnd(); var provider = ExtractJsonString(body, "provider"); var model = ExtractJsonString(body, "modelId") ?? ExtractJsonString(body, "model"); if (string.IsNullOrWhiteSpace(provider)) return V(Error(400, "provider required")); a.SwitchLlm(provider, model ?? a.CurrentLlm.ModelId); return V(Ok()); });
        app.MapPost($"{p}/provider/switch", (ctx, _) => { using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8); var body = r.ReadToEnd(); var provider = ExtractJsonString(body, "provider"); if (string.IsNullOrWhiteSpace(provider)) return V(Error(400, "provider required")); a.SwitchLlm(provider, a.CurrentLlm.ModelId); return V(Ok()); });
        app.MapPost($"{p}/session/{{id}}/create", (_, _) => V(Ok()));
        app.MapPost($"{p}/session/{{id}}/delete", (_, _) => V(Ok()));
        app.MapPost($"{p}/session/{{id}}/save", (_, _) => V(Ok()));
        app.MapGet($"{p}/session/{{id}}/messages", (_, _) => V(Json("[]")));
        app.MapPost($"{p}/session/{{id}}/retry", (_, _) => V(Ok()));
        app.MapPost($"{p}/session/{{id}}/compact", (_, _) => V(Json("{\"compressedCount\":0,\"summary\":null,\"tokensSaved\":0}")));
        app.MapPost($"{p}/session/{{id}}/message", async (ctx, ct) =>
        {
            using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
            var message = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(message)) return Error(400, "empty message");
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                await turnLock.WaitAsync(ct);
                try { await a.RunTurn(message, llm, tr, ct, async (k, t) => { await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"event: {k}\ndata: {t ?? ""}\n\n"), ct); }); }
                catch (Exception ex) { await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"event: error\ndata: {ex.Message}\n\n"), ct); }
                finally { turnLock.Release(); await pipe.Writer.CompleteAsync(); }
            }, ct);
            return new HttpResponse { StatusCode = 200, Headers = [new("Content-Type", "text/event-stream"), new("Cache-Control", "no-cache")], BodyStream = pipe.Reader.AsStream() };
        });
    }

    private WebApp BuildWebApp()
    {
        var app = new WebApp(new SvcContainer(), new WebAppOptions());
        AddEndpoints(app, _agent, _llmClient, _toolRunner);
        return app;
    }

    private static ValueTask<HttpResponse> V(HttpResponse r) => ValueTask.FromResult(r);
    private static HttpResponse Ok() => new() { StatusCode = 200, Body = "{\"status\":\"ok\"}"u8.ToArray(), Headers = [new("Content-Type", "application/json; charset=utf-8")] };
    private static HttpResponse Error(int code, string msg) => new() { StatusCode = code, Body = Encoding.UTF8.GetBytes($"{{\"error\":\"{msg}\"}}"), Headers = [new("Content-Type", "application/json; charset=utf-8")] };
    private static HttpResponse Json(string body) => new() { StatusCode = 200, Body = Encoding.UTF8.GetBytes(body), Headers = [new("Content-Type", "application/json; charset=utf-8")] };
    private static string? ExtractJsonString(string json, string key) { var p = $"\"{key}\":\""; var s = json.IndexOf(p, StringComparison.Ordinal); if (s < 0) return null; s += p.Length; var e = json.IndexOf('"', s); return e > s ? json[s..e] : null; }
    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static IPEndPoint ParseEndpoint(string uri) { var u = new Uri(uri); return new IPEndPoint(IPAddress.Loopback, u.IsDefaultPort || u.Port < 0 ? 80 : u.Port); }
    private static readonly string ProviderTemplates = """[{"name":"openai","label":"OpenAI","baseUrl":"https://api.openai.com/v1","apiFormat":"openai"},{"name":"anthropic","label":"Anthropic","baseUrl":"https://api.anthropic.com","apiFormat":"anthropic"},{"name":"deepseek","label":"DeepSeek","baseUrl":"https://api.deepseek.com/v1","apiFormat":"openai"},{"name":"groq","label":"Groq","baseUrl":"https://api.groq.com/openai/v1","apiFormat":"openai"},{"name":"ollama","label":"Ollama","baseUrl":"http://localhost:11434/v1","apiFormat":"openai"}]""";
    public async ValueTask DisposeAsync() { if (_webServer is not null) await _webServer.DisposeAsync(); }
}
