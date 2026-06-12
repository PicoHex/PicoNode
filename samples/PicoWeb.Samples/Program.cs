using System.Net;
using PicoDI.Abs;
using PicoWeb;
using PicoWeb.Samples;

// ── PicoDI container ───────────────────────────────────────
//
// ISvcContainer is provided by PicoDI (the default container).
// Each request receives its own ISvcScope via ScopeMiddleware.
//
//   using PicoDI;
//   var container = new SvcContainer();
//   container.RegisterScoped<IMyService, MyService>();
//
// Pass the container to WebServer:
//   new WebServer(app, options, container)

ISvcContainer? container = null;

// ── Build the WebApp and start the server ─────────────────

var app = ShowcaseApp.Create();

await using var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 7004) },
    container
);

await server.StartAsync();

Console.WriteLine($"PicoWeb showcase listening on {server.LocalEndPoint}");
Console.WriteLine("GET     /                    -> static landing page");
Console.WriteLine("GET     /api/showcase        -> capability summary");
Console.WriteLine("GET     /api/preferences     -> cookie-backed theme");
Console.WriteLine("POST    /api/preferences/*   -> set theme cookie");
Console.WriteLine("GET     /api/content         -> compression demo");
Console.WriteLine("POST    /api/uploads         -> multipart form-data");
Console.WriteLine("OPTIONS /api/*               -> CORS preflight");
Console.WriteLine();
Console.WriteLine("DI: pass ISvcContainer to WebServer for per-request scopes.");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();

await server.StopAsync();
