using PicoNode.Web;
using PicoWeb;

var api = new WebApiBuilder()
    .ConfigureApp(o => new WebAppOptions { ServerHeader = "PicoWeb" })
    .Build();

api.MapGet("/api/showcase", (WebContext ctx) =>
    Results.Json(200, """
        {"status":"ok","path":"/api/showcase"}
        """u8.ToArray()));

await api.RunAsync("http://+:7004");
