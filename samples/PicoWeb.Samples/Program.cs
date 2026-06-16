// PicoWeb Sample — 3 controller discovery patterns via interactive landing page.
// Open http://localhost:7004/ and use the UI to test each pattern.

using System.Net;
using PicoDI;
using PicoDI.Abs;
using PicoWeb;

var container = new SvcContainer();
container.RegisterScoped<PicoWeb.Samples.Controllers.UsersController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.ProductsController>();
container.RegisterScoped<PicoWeb.Samples.Controllers.PostsController>();
container.Build();

// ShowcaseApp provides: static files, /api/showcase, /api/preferences, /api/content, /api/uploads
var app = PicoWeb.Samples.ShowcaseApp.Create(container);

// Add generated controller endpoints on top
EndpointRegistrar.RegisterAll(app);

var server = new WebServer(
    app,
    new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, 7004) }
);
await server.StartAsync();
Console.WriteLine($"Listening on {server.LocalEndPoint}");
Console.WriteLine("Open http://localhost:7004/ in your browser");
await Task.Delay(Timeout.Infinite);
await server.DisposeAsync();
