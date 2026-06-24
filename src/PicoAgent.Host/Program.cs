using PicoNode.Web;
using PicoNode.Http;
using PicoWeb;
using PicoNode.AI;
using static PicoAgent.FileSystemConstants;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8080;
Console.WriteLine($"PicoAgent Host starting on port {port}...");

// Wire up the agent loop
var httpClient = new HttpClient();
var llmClient = new AnthropicLLmClient(httpClient);
var registry = new CapabilityRegistry();
registry.Scan(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    AgentHomeDir));
var runner = new CapabilityRunner();
var model = new Model
{
    Id = "claude-sonnet-4-20250514",
    BaseUrl = "https://api.anthropic.com",
    Api = AiApiFormat.AnthropicMessages,
    Provider = "anthropic",
    MaxTokens = 4096,
};
var loop = new AgentLoop(llmClient, registry, runner, model);
var host = new AgentHost(loop);

var api = new WebApiBuilder()
    .ConfigureApp(o => new WebAppOptions { ServerHeader = "PicoAgent" })
    .Build();

api.MapPost("/session/{id}/message", async (WebContext ctx, CancellationToken ct) =>
{
    using var reader = new StreamReader(ctx.Request.BodyStream);
    var body = await reader.ReadToEndAsync(ct);
    var response = await host.ProcessMessageAsync(body, ct, ctx.RouteValues["id"]);
    return new HttpResponse
    {
        StatusCode = 200,
        Headers = [new("Content-Type", "application/json")],
        Body = Encoding.UTF8.GetBytes($"{{\"response\":\"{EscapeJson(response)}\"}}"),
    };
});

// v1: stub SSE endpoint. v2: use SseConnection to stream AgentLoop events in real-time.
api.MapGet("/session/{id}/events", (WebContext ctx, CancellationToken ct) =>
{
    return ValueTask.FromResult(new HttpResponse
    {
        StatusCode = 200,
        Headers = [new("Content-Type", "text/event-stream"), new("Cache-Control", "no-cache")],
        Body = "data: {\"type\":\"ready\"}\n\n"u8.ToArray(),
    });
});

api.MapPost("/reload", (WebContext ctx, CancellationToken ct) =>
{
    registry.Scan(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        AgentHomeDir));
    return ValueTask.FromResult(new HttpResponse
    {
        StatusCode = 200,
        Body = "{\"status\":\"reloaded\"}"u8.ToArray(),
    });
});

var url = $"http://+:{port}";
Console.WriteLine($"Listening on {url}");
await api.RunAsync(url);

static string EscapeJson(string s)
{
    var sb = new StringBuilder(s.Length + 2);
    foreach (var c in s)
    {
        switch (c)
        {
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                else sb.Append(c);
                break;
        }
    }
    return sb.ToString();
}
