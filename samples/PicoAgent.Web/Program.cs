using PicoNode.Agent.Domain;

var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

var (server, _) = await PicoAgent.Bootstrap.StartAsync(homeDir, ["http://localhost:19998"]);
Console.WriteLine($"Backend on http://localhost:19998");

// Frontend + proxy on port 9000
var container = new SvcContainer(); container.Build();
var app = new WebApp(container, new WebAppOptions { ServerHeader = "PicoAgent.Web" });

// Proxy /api/* → backend
var apiClient = new HttpClient { BaseAddress = new Uri("http://localhost:19998/") };
app.Use(async (ctx, next, ct) =>
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
    return new HttpResponse
    {
        StatusCode = (int)resp.StatusCode, Body = body,
        Headers = [new("Content-Type", resp.Content.Headers.ContentType?.MediaType ?? "application/json; charset=utf-8")],
    };
});

// Static files
var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot)) app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

var ws = new WebServer(app, new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000) });
await ws.StartAsync();
Console.WriteLine("Frontend on http://localhost:9000");
await Task.Delay(-1);
