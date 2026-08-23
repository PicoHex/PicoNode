using System.Net.Sockets;

namespace PicoWeb.Integration.Tests;

public sealed class ControllerEndpointsTests
{
    [Test]
    public async Task EndpointRegistrar_registers_generated_controller_routes()
    {
        // Controllers.Gen generates EndpointRegistrar + a [ModuleInitializer]
        // that registers controllers in DI. Consumers must call
        // EndpointRegistrar.RegisterAll(app) to wire the routes (README
        // documents this). Regression: PicoWeb.Samples never called it, so
        // the generated endpoints were dead code retained in AOT binaries.
        var port = GetRandomPort();
        var container = new SvcContainer();
        container.Build();
        var app = new WebApp(container);
        EndpointRegistrar.RegisterAll(app);

        await using var server = new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
        await server.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{port}/api/widgets/item/42");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).Contains("item 42");
    }

    private static int GetRandomPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
