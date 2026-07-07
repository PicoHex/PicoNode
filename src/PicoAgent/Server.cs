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

    public int Port => _webServer?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;

    public Server(
        DomainAgent agent,
        DomainInterfaces.ILlmClient llmClient,
        DomainInterfaces.IToolRunner toolRunner
    )
    {
        _agent = agent;
        _llmClient = llmClient;
        _toolRunner = toolRunner;
    }

    public async Task ListenAsync(string uri)
    {
        if (_isListening)
            throw new InvalidOperationException("Server is already listening");
        _isListening = true;
        _agent.Start();
        var app = BuildWebApp();
        var ep = ParseEndpoint(uri);
        _webServer = new WebServer(app, new WebServerOptions { Endpoint = ep });
        await _webServer.StartAsync(CancellationToken.None);
    }

    private WebApp BuildWebApp()
    {
        var app = new WebApp(new SvcContainer(), new WebAppOptions());

        app.MapGet(
            "/health",
            (_, _) =>
            {
                var body = Encoding.UTF8.GetBytes(
                    "{\"status\":\"ok\",\"model\":\"" + _agent.CurrentLlm.ModelId + "\"}"
                );
                return ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        Body = body,
                        Headers = [new("Content-Type", "application/json; charset=utf-8")],
                    }
                );
            }
        );

        app.MapPost("/reload", (_, _) => ValueTask.FromResult(Ok()));

        app.MapPost(
            "/thinking",
            (ctx, ct) =>
            {
                using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var body = reader.ReadToEnd();
                var level = ExtractJsonString(body, "level");
                if (level is { Length: > 0 })
                {
                    var thinkingLevel = level.ToLower() switch
                    {
                        "minimal" => ThinkingLevel.Minimal,
                        "low" => ThinkingLevel.Low,
                        "medium" => ThinkingLevel.Medium,
                        "high" => ThinkingLevel.High,
                        "xhigh" => ThinkingLevel.XHigh,
                        _ => _agent.CurrentLlm.ThinkingLevel,
                    };
                    _agent.CurrentLlm.ThinkingLevel = thinkingLevel;
                }
                return ValueTask.FromResult(Ok());
            }
        );

        app.MapPost(
            "/model/switch",
            async (ctx, ct) =>
            {
                using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                var provider = ExtractJsonString(body, "provider");
                var model = ExtractJsonString(body, "model");
                if (string.IsNullOrWhiteSpace(provider))
                    return Error(400, "provider required");
                _agent.SwitchLlm(provider, model ?? _agent.CurrentLlm.ModelId);
                return Ok();
            }
        );

        app.MapPost(
            "/session/{id}/message",
            async (ctx, ct) =>
            {
                using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var message = await reader.ReadToEndAsync(ct);
                if (string.IsNullOrWhiteSpace(message))
                    return Error(400, "empty message");

                var pipe = new Pipe();
                _ = Task.Run(() => RunTurnIntoPipe(message, pipe.Writer, ct), ct);
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

        return app;
    }

    private async Task RunTurnIntoPipe(string message, PipeWriter writer, CancellationToken ct)
    {
        await _turnLock.WaitAsync(ct);
        try
        {
            await _agent.RunTurn(
                message,
                _llmClient,
                _toolRunner,
                ct,
                async (kind, text) =>
                {
                    var sse = $"event: {kind}\ndata: {text ?? ""}\n\n";
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(sse), ct);
                }
            );
        }
        catch (Exception ex)
        {
            var err = $"event: error\ndata: {ex.Message}\n\n";
            await writer.WriteAsync(Encoding.UTF8.GetBytes(err), ct);
        }
        finally
        {
            _turnLock.Release();
            await writer.CompleteAsync();
        }
    }

    private static HttpResponse Ok() =>
        new()
        {
            StatusCode = 200,
            Body = "{\"status\":\"ok\"}"u8.ToArray(),
            Headers = [new("Content-Type", "application/json; charset=utf-8")],
        };

    private static HttpResponse Error(int code, string msg) =>
        new()
        {
            StatusCode = code,
            Body = Encoding.UTF8.GetBytes($"{{\"error\":\"{msg}\"}}"),
            Headers = [new("Content-Type", "application/json; charset=utf-8")],
        };

    private static string? ExtractJsonString(string json, string key)
    {
        var pattern = $"\"{key}\":\"";
        var start = json.IndexOf(pattern, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += pattern.Length;
        var end = json.IndexOf('"', start);
        return end > start ? json[start..end] : null;
    }

    private static IPEndPoint ParseEndpoint(string uri)
    {
        var u = new Uri(uri);
        var port = u.IsDefaultPort || u.Port < 0 ? 80 : u.Port;
        return new IPEndPoint(IPAddress.Loopback, port);
    }

    public async ValueTask DisposeAsync()
    {
        if (_webServer is not null)
            await _webServer.DisposeAsync();
    }
}
