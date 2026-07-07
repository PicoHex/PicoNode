using PicoNode.Agent.Domain;

var homeDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(homeDir);
Directory.CreateDirectory(Path.Combine(homeDir, FileSystemConstants.SessionsDir));

var (server, _) = await PicoAgent.Bootstrap.StartAsync(homeDir, ["http://localhost:19998"]);
Console.WriteLine($"Backend on http://localhost:19998");

// Frontend on port 9000
var container = new SvcContainer(); container.Build();
var app = new WebApp(container, new WebAppOptions { ServerHeader = "PicoAgent.Web" });

var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwroot))
    app.Use(new StaticFileMiddleware(wwwroot).InvokeAsync);

var ws = new WebServer(app, new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 9000) });
await ws.StartAsync();
Console.WriteLine("Frontend on http://localhost:9000");
await Task.Delay(-1);
