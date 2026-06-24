using PicoNode.Web;
using PicoNode.Http;
using PicoWeb;
using PicoNode.AI;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8080;
Console.WriteLine($"PicoAgent Host starting on port {port}...");

var api = new WebApiBuilder()
    .ConfigureApp(o => new WebAppOptions { ServerHeader = "PicoAgent" })
    .Build();

api.MapPost("/session/{id}/message", (WebContext ctx, CancellationToken ct) =>
{
    return ValueTask.FromResult(new HttpResponse
    {
        StatusCode = 200,
        Headers = [new("Content-Type", "application/json")],
        Body = "{\"status\":\"ok\"}"u8.ToArray(),
    });
});

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
    // v1: reload signal — Agent rescans capabilities + knowledge on next turn
    return ValueTask.FromResult(new HttpResponse
    {
        StatusCode = 200,
        Body = "{\"status\":\"reloaded\"}"u8.ToArray(),
    });
});

var url = $"http://+:{port}";
Console.WriteLine($"Listening on {url}");
await api.RunAsync(url);
