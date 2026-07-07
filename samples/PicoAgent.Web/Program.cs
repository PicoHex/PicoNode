using PicoNode.Agent.Domain;

var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);

var (server, _) = await PicoAgent.Bootstrap.StartAsync(homeDir, []);

// Add static file middleware to the same server
var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot))
{
    var staticFiles = new StaticFileMiddleware(wwwroot);
    // Re-register on a new endpoint — use a separate WebServer on port 9000
    // since we can't add middleware after ListenAsync
}

// Start static files on port 9000
var container = new SvcContainer(); container.Build();
var app2 = new WebApp(container, new WebAppOptions());
if (Directory.Exists(wwwroot)) app2.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

// Proxy /api/* to backend
var apiClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}/") };

// Static files first (runs last in LIFO)
if (Directory.Exists(wwwroot)) app2.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

// Proxy last (runs first in LIFO — catches /api/* before static files)
app2.Use(async (ctx, next, ct) =>
{
    if (!ctx.Request.Path.StartsWith("/api/")) return await next(ctx, ct);
    using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), ctx.Request.Path);
    if (ctx.Request.BodyStream.Length > 0)
    {
        var ms = new MemoryStream(); await ctx.Request.BodyStream.CopyToAsync(ms, ct); ms.Position = 0;
        req.Content = new StreamContent(ms);
    }
    var resp = await apiClient.SendAsync(req, ct);
    var body = await resp.Content.ReadAsByteArrayAsync(ct);
    return new HttpResponse { StatusCode = (int)resp.StatusCode, Body = body, Headers = [new("Content-Type", resp.Content.Headers.ContentType?.MediaType ?? "application/json")] };
});

var ws = new WebServer(app2, new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000) });
await ws.StartAsync();
Console.WriteLine($"Backend {server.Port}, Frontend 9000");
await Task.Delay(-1);
