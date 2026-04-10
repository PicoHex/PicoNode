using System.Net;
using System.Text;
using PicoNode.Http;
using PicoNode.Web;
using PicoNode.WebServer;

var app = new WebApp(new WebAppOptions { ServerHeader = "PicoNode.Samples.Web", });

app.Use(
    async (context, next, cancellationToken) =>
    {
        var response = await next(context, cancellationToken);
        var headers = new List<KeyValuePair<string, string>>(response.Headers)
        {
            new("X-Request-Path", context.Path),
        };
        return new HttpResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Headers = headers,
            Body = response.Body,
        };
    }
);

app.MapGet(
    "/",
    static (_, _) => ValueTask.FromResult(WebResults.Text(200, "hello from PicoNode.Web", "OK"))
);

app.MapGet(
    "/users/{id}",
    static (context, _) =>
    {
        var id = context.RouteValues["id"];
        return ValueTask.FromResult(WebResults.Json(200, $$"""{"id":"{{id}}"}""", "OK"));
    }
);

app.MapPost(
    "/echo",
    static (context, _) =>
    {
        var body = Encoding.UTF8.GetString(context.Request.Body.Span);
        return ValueTask.FromResult(WebResults.Text(200, body, "OK"));
    }
);

await using var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 7004) }
);

await server.StartAsync();

Console.WriteLine($"Web sample listening on {server.LocalEndPoint}");
Console.WriteLine("GET  /           -> text greeting");
Console.WriteLine("GET  /users/{id} -> JSON user by id");
Console.WriteLine("POST /echo       -> echoes request body");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();

await server.StopAsync();
