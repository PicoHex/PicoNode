using System.Net;
using PicoDI;
using PicoWeb;
using PicoWeb.Samples;

var container = new SvcContainer();
var app = ShowcaseApp.Create(container);

await using var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 7004) }
);

await server.StartAsync();

Console.WriteLine($"PicoWeb showcase listening on {server.LocalEndPoint}");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();

await server.StopAsync();
