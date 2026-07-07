namespace PicoAgent;

using DomainAgent = PicoNode.Agent.Domain.Agent;
using DomainInterfaces = PicoNode.Agent.Domain;

public sealed class Server : IAsyncDisposable
{
    private readonly DomainAgent _agent;
    private readonly DomainInterfaces.ILlmClient _llmClient;
    private readonly DomainInterfaces.IToolRunner _toolRunner;
    private WebServer? _webServer;

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

        app.MapPost(
            "/session/{id}/message",
            async (ctx, ct) =>
            {
                using var reader = new StreamReader(ctx.Request.BodyStream, Encoding.UTF8);
                var message = await reader.ReadToEndAsync(ct);

                if (string.IsNullOrWhiteSpace(message))
                {
                    return new HttpResponse
                    {
                        StatusCode = 400,
                        Body = "{\"error\":\"empty message\"}"u8.ToArray(),
                        Headers = [new("Content-Type", "application/json; charset=utf-8")],
                    };
                }

                var result = await _agent.RunTurn(message, _llmClient, _toolRunner, ct);
                var lastText =
                    result
                        .LastOrDefault(m => m.Role == "assistant")
                        ?.ContentBlocks?.FirstOrDefault(cb => cb.Type == "text")
                        ?.Text
                    ?? "";

                var body = Encoding.UTF8.GetBytes(
                    "{\"reply\":\"" + lastText.Replace("\"", "\\\"") + "\"}"
                );
                return new HttpResponse
                {
                    StatusCode = 200,
                    Body = body,
                    Headers = [new("Content-Type", "application/json; charset=utf-8")],
                };
            }
        );

        return app;
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
