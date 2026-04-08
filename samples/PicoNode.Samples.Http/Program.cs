using System.Net;
using System.Text;
using global::PicoNode;
using global::PicoNode.Http;

var node = new TcpNode(
    new TcpNodeOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 7003),
        ConnectionHandler = new HttpConnectionHandler(
            new HttpConnectionHandlerOptions
            {
                RequestHandler = CreateRouter().HandleAsync,
                ServerHeader = "PicoNode.Samples.Http",
            }
        ),
        EnableKeepAlive = true,
    }
);

await node.StartAsync();

Console.WriteLine($"HTTP sample listening on {node.LocalEndPoint}");
Console.WriteLine("GET  /        -> returns a text greeting");
Console.WriteLine("POST /echo    -> echoes the request body");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();

await node.DisposeAsync();

static HttpRouter CreateRouter() =>
    new(
        new HttpRouterOptions
        {
            Routes =
            [
                new HttpRoute
                {
                    Method = "GET",
                    Path = "/",
                    Handler = static (_, _) =>
                        ValueTask.FromResult(CreateTextResponse(200, "OK", "hello from PicoNode.Http")),
                },
                new HttpRoute
                {
                    Method = "POST",
                    Path = "/echo",
                    Handler = static (request, _) =>
                        ValueTask.FromResult(
                            new HttpResponse
                            {
                                StatusCode = 200,
                                ReasonPhrase = "OK",
                                Headers =
                                [
                                    new KeyValuePair<string, string>("Content-Type", "text/plain"),
                                    new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"),
                                ],
                                Body = request.Body.ToArray(),
                            }
                        ),
                },
            ],
            FallbackHandler = static (_, _) =>
                ValueTask.FromResult(CreateTextResponse(404, "Not Found", "not found")),
        }
    );

static HttpResponse CreateTextResponse(int statusCode, string reasonPhrase, string body) =>
    new()
    {
        StatusCode = statusCode,
        ReasonPhrase = reasonPhrase,
        Headers =
        [
            new KeyValuePair<string, string>("Content-Type", "text/plain"),
            new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"),
        ],
        Body = Encoding.UTF8.GetBytes(body),
    };
