using System.Net;
using PicoDI;
using PicoDI.Abs;
using PicoNode.Web;
using PicoWeb;

// ── PicoWeb Sample — DI First WebAPI ────────────────────
//
// Core features demonstrated:
//   1. Controllers/ folder convention → auto-discovered by Controllers.Gen
//   2. [Route] / [HttpGet] attribute override
//   3. [HttpPost] / [HttpDelete] / [HttpPatch] verb attributes
//   4. Inline MapXX typed endpoints (AOT-safe, no reflection)
//   5. DI with RegisterScoped<T>() + PicoDI.Gen
//   6. AOT publish: dotnet publish -c Release -r win-x64 -p:PublishAot=true

var container = new SvcContainer();
container.RegisterScoped<PicoWeb.Samples.Controllers.UsersController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.ProductsController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.PostsController>();
container.Build();

var app = new WebApp(container);

// ── Generated controller endpoints ──
EndpointRegistrar.RegisterAll(app);

// ── Inline endpoints (AOT-safe WebRequestHandler lambda) ──
app.MapGet("/api/health", static (WebContext ctx, CancellationToken _) =>
    ValueTask.FromResult(WebResults.Json(200, """{"status":"ok"}""", "OK")));

// ── Start ──
var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 7004) }
);
await server.StartAsync();
Console.WriteLine($"Listening on {server.LocalEndPoint}");
Console.WriteLine();
Console.WriteLine("Endpoints:");
Console.WriteLine("  Convention:         GET  /api/users/user/{id}");
Console.WriteLine("  Route attribute:    GET  /api/v2/products/{category}/{id}");
Console.WriteLine("  Verb attributes:    POST /api/posts");
Console.WriteLine("                      DELETE /api/posts/{id}");
Console.WriteLine("                      PATCH /api/posts/{id}");
Console.WriteLine("                      POST  /api/posts/{id}/restore");
Console.WriteLine("  Health check:       GET  /api/health");
Console.WriteLine();
Console.WriteLine("Try:");
Console.WriteLine("  curl http://localhost:7004/api/users/user/42");
Console.WriteLine("  curl http://localhost:7004/api/v2/products/electronics/99");
Console.WriteLine("  curl -X PATCH http://localhost:7004/api/posts/7");

await Task.Delay(Timeout.Infinite);
await server.DisposeAsync();
