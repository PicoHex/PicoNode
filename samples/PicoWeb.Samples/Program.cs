using PicoNode.Web;
using PicoWeb;

// ── WebApiBuilder (DI First) ─────────────────────────────
// PicoWeb + PicoJetson + PicoDI provide an AOT-ready web framework.
// See the README for controller-based and MapXX endpoint patterns.

var api = new WebApiBuilder()
    .ConfigureApp(o => new WebAppOptions { ServerHeader = "PicoWeb" })
    .Build();

api.MapGet("/api/health", (WebContext ctx) =>
    Results.Json(200, """{"status":"ok","source":"MapGet"}"""u8.ToArray()));

await api.RunAsync("http://+:7004");
