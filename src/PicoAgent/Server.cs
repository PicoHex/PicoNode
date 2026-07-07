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

    public async Task ListenAsync(string uri, WebMiddleware? preMiddleware = null)
    {
        if (_isListening) throw new InvalidOperationException("Already listening");
        _isListening = true; _agent.Start();
        _webServer = new WebServer(BuildWebApp(preMiddleware), new WebServerOptions { Endpoint = ParseEndpoint(uri) });
        await _webServer.StartAsync(CancellationToken.None);
    }

    private WebApp BuildWebApp(WebMiddleware? preMiddleware = null)
    {
        var app = new WebApp(new SvcContainer(), new WebAppOptions());
        AddEndpoints(app, _agent, _llmClient, _toolRunner, _turnLock, ref _systemPrompt);

        app.MapGet("/health", (_, _) => V(Json($"{{\"status\":\"ok\",\"model\":\"{a.CurrentLlm.ModelId}\",\"provider\":\"{a.CurrentLlm.ProviderName}\"}}")));

        app.MapGet("/models", (_, _) => V(Json("[]")));
        app.MapGet("/sessions", (_, _) => V(Json("[\"default\"]")));

        app.MapGet("/config/status", (_, _) => V(Json($"{{\"configured\":true,\"model\":\"{a.CurrentLlm.ModelId}\",\"provider\":\"{a.CurrentLlm.ProviderName}\",\"providers\":[{string.Join(",", a.Llms.Select(l => $"\"{l.ProviderName}\""))}],\"thinkingEnabled\":{a.CurrentLlm.ThinkingEnabled.ToString().ToLower()},\"thinkingLevel\":\"{a.CurrentLlm.ThinkingLevel.ToString().ToLowerInvariant()}\",\"maxTokens\":{a.CurrentLlm.MaxTokens}}}")));
        app.MapGet("/config/providers", (_, _) => V(Json(ProviderTemplates)));
        app.MapPost("/config/validate", (_, _) => V(Json("[]")));
        app.MapPost("/config", (_, _) => V(Json("{\"status\":\"saved\"}")));

        app.MapGet("/system-prompt", (_, _) => V(Json($"{{\"prompt\":\"{EscapeJson(_systemPrompt ?? "")}\"}}")));
        app.MapPost("/system-prompt", async (ctx, _) => { using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8); var body = await r.ReadToEndAsync(); _systemPrompt = ExtractJsonString(body, "prompt"); return Ok(); });

        app.MapPost("/reload", (_, _) => V(Ok()));
        app.MapPost("/thinking", (ctx, _) => { using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8); var body = r.ReadToEnd(); var lvl = ExtractJsonString(body, "level"); if (lvl is { Length: > 0 }) { a.CurrentLlm.ThinkingLevel = lvl.ToLower() switch { "minimal" => ThinkingLevel.Minimal, "low" => ThinkingLevel.Low, "medium" => ThinkingLevel.Medium, "high" => ThinkingLevel.High, "xhigh" => ThinkingLevel.XHigh, _ => a.CurrentLlm.ThinkingLevel }; } return V(Ok()); });

        app.MapPost("/model/switch", (ctx, _) => { using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8); var body = r.ReadToEnd(); var provider = ExtractJsonString(body, "provider"); var model = ExtractJsonString(body, "modelId") ?? ExtractJsonString(body, "model"); if (string.IsNullOrWhiteSpace(provider)) return V(Error(400, "provider required")); a.SwitchLlm(provider, model ?? a.CurrentLlm.ModelId); return V(Ok()); });

        app.MapPost("/provider/switch", (ctx, _) => { using var r = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8); var body = r.ReadToEnd(); var provider = ExtractJsonString(body, "provider"); if (string.IsNullOrWhiteSpace(provider)) return V(Error(400, "provider required")); a.SwitchLlm(provider, a.CurrentLlm.ModelId); return V(Ok()); });

        app.MapPost("/session/{id}/create", (_, _) => V(Ok()));
        app.MapPost("/session/{id}/delete", (_, _) => V(Ok()));
        app.MapPost("/session/{id}/save", (_, _) => V(Ok()));
        app.MapGet("/session/{id}/messages", (_, _) => V(Json("[]")));
        app.MapPost("/session/{id}/retry", (_, _) => V(Ok()));
        app.MapPost("/session/{id}/compact", (_, _) => V(Json("{\"compressedCount\":0,\"summary\":null,\"tokensSaved\":0}")));

        app.MapPost("/session/{id}/message", async (ctx, ct) =>
        {
            using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
            var message = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(message)) return Error(400, "empty message");
            var pipe = new Pipe();
            _ = Task.Run(() => RunTurnIntoPipe(message, pipe.Writer, ct), ct);
            return new HttpResponse { StatusCode = 200, Headers = [new("Content-Type", "text/event-stream"), new("Cache-Control", "no-cache")], BodyStream = pipe.Reader.AsStream() };
        });

        if (preMiddleware is not null) app.Use(preMiddleware);
        return app;
    }

    private async Task RunTurnIntoPipe(string message, PipeWriter writer, CancellationToken ct)
    {
        await _turnLock.WaitAsync(ct);
        try { await _agent.RunTurn(message, _llmClient, _toolRunner, ct, async (k, t) => { await writer.WriteAsync(Encoding.UTF8.GetBytes($"event: {k}\ndata: {t ?? ""}\n\n"), ct); }); }
        catch (Exception ex) { await writer.WriteAsync(Encoding.UTF8.GetBytes($"event: error\ndata: {ex.Message}\n\n"), ct); }
        finally { _turnLock.Release(); await writer.CompleteAsync(); }
    }

    private static ValueTask<HttpResponse> V(HttpResponse r) => ValueTask.FromResult(r);
    private HttpResponse Ok() => new() { StatusCode = 200, Body = "{\"status\":\"ok\"}"u8.ToArray(), Headers = [new("Content-Type", "application/json; charset=utf-8")] };
    private static HttpResponse Error(int code, string msg) => new() { StatusCode = code, Body = Encoding.UTF8.GetBytes($"{{\"error\":\"{msg}\"}}"), Headers = [new("Content-Type", "application/json; charset=utf-8")] };
    private static HttpResponse Json(string body) => new() { StatusCode = 200, Body = Encoding.UTF8.GetBytes(body), Headers = [new("Content-Type", "application/json; charset=utf-8")] };
    private static string? ExtractJsonString(string json, string key) { var p = $"\"{key}\":\""; var s = json.IndexOf(p, StringComparison.Ordinal); if (s < 0) return null; s += p.Length; var e = json.IndexOf('"', s); return e > s ? json[s..e] : null; }
    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    private static IPEndPoint ParseEndpoint(string uri) { var u = new Uri(uri); return new IPEndPoint(IPAddress.Loopback, u.IsDefaultPort || u.Port < 0 ? 80 : u.Port); }
    private static readonly string ProviderTemplates = """[{"name":"openai","label":"OpenAI","baseUrl":"https://api.openai.com/v1","apiFormat":"openai"},{"name":"anthropic","label":"Anthropic","baseUrl":"https://api.anthropic.com","apiFormat":"anthropic"},{"name":"deepseek","label":"DeepSeek","baseUrl":"https://api.deepseek.com/v1","apiFormat":"openai"},{"name":"groq","label":"Groq","baseUrl":"https://api.groq.com/openai/v1","apiFormat":"openai"},{"name":"ollama","label":"Ollama","baseUrl":"http://localhost:11434/v1","apiFormat":"openai"}]""";
    public async ValueTask DisposeAsync() { if (_webServer is not null) await _webServer.DisposeAsync(); }
}
