using System.Net;
using PicoDI;
using PicoDI.Abs;
using PicoNode.Web;
using PicoWeb;

// ── PicoWeb Sample — DI First WebAPI ────────────────────
//
// Demonstrates three controller discovery patterns (by Controllers.Gen):
//
//   Pattern 1 — Convention (Controllers/ folder + method prefix):
//     UsersController.GetUser(int id)
//     → GET /api/users/user/{id}
//
//   Pattern 2 — Route attribute override:
//     [Route("api/v2/products")]
//     ProductsController.GetProduct(string category, int id)
//     → GET /api/v2/products/{category}/{id}
//
//   Pattern 3 — HTTP verb attributes + mixed:
//     [HttpPost]  PostsController.CreatePost(string title)
//     → POST /api/posts
//     [HttpDelete] PostsController.DeletePost(int id)
//     → DELETE /api/posts/{id}
//     [HttpPatch]  PostsController.PatchPost(int id)
//     → PATCH /api/posts/{id}
//
// Plus inline MapXX endpoints.

var container = new SvcContainer();
container.RegisterScoped<PicoWeb.Samples.Controllers.UsersController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.ProductsController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.PostsController>();
container.Build();
var app = new WebApp(container);

// ── Generated controller endpoints ──
EndpointRegistrar.RegisterAll(app);

// ── Inline endpoints ──
app.MapGet("/api/health", (WebContext ctx, CancellationToken _) =>
    ValueTask.FromResult(WebResults.Json(200, """{"status":"ok"}""", "OK")));

app.MapGet("/api/echo/{msg}", (WebContext ctx, CancellationToken _) =>
{
    var msg = ctx.RouteValues.TryGetValue("msg", out var v) ? v : "";
    return ValueTask.FromResult(WebResults.Json(200, $$"""{"echo":"{{msg}}"}""", "OK"));
});

// ── Start ──
var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 7004) }
);
await server.StartAsync();
Console.WriteLine($"Listening on {server.LocalEndPoint}");
Console.WriteLine();
Console.WriteLine("Endpoints:");
Console.WriteLine("  GET  /api/health");
Console.WriteLine("  GET  /api/echo/{msg}");
Console.WriteLine("  GET  /api/users/user/{id}          ← convention");
Console.WriteLine("  GET  /api/v2/products/{cat}/{id}   ← [Route] + [HttpGet]");
Console.WriteLine("  GET  /api/posts/list               ← convention (GetList)");
Console.WriteLine("  POST /api/posts                    ← [HttpPost]");
Console.WriteLine("  DELETE /api/posts/{id}             ← [HttpDelete]");
Console.WriteLine("  PATCH /api/posts/{id}              ← [HttpPatch]");
Console.WriteLine("  POST /api/posts/{id}/restore       ← [HttpPost(\"{id}/restore\")]");
Console.WriteLine();
Console.WriteLine("Try: curl http://localhost:7004/api/users/user/42");
Console.WriteLine("     curl http://localhost:7004/api/v2/products/electronics/99");
Console.WriteLine("     curl -X POST http://localhost:7004/api/posts");

await Task.Delay(Timeout.Infinite);
await server.DisposeAsync();
