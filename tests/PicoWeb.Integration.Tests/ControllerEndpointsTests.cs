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
        var port = TestSupport.GetRandomPort();
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

    [Test]
    public async Task Post_with_simple_parameter_binds_from_query()
    {
        // Audit F-02: [HttpPost] Create(string title) emits route /api/posts —
        // the parameter has no route segment, so it must bind from the query
        // string (RouteValues → Query). Regression: the generated handler used
        // the unguarded RouteValues indexer → KeyNotFoundException → 500.
        var port = TestSupport.GetRandomPort();
        await using var server = CreateServer(port);
        await server.StartAsync();

        using var client = new HttpClient();
        using var content = new StringContent(string.Empty);
        var response = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/posts?title=hello",
            content
        );
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        await Assert.That(body).Contains("hello");
    }

    [Test]
    public async Task Post_without_the_parameter_returns_400_not_500()
    {
        // Neither a route segment nor a query value exists → the generated
        // guard must short-circuit with 400 instead of throwing.
        var port = TestSupport.GetRandomPort();
        await using var server = CreateServer(port);
        await server.StartAsync();

        using var client = new HttpClient();
        using var content = new StringContent(string.Empty);
        var response = await client.PostAsync($"http://127.0.0.1:{port}/api/posts", content);

        await Assert.That((int)response.StatusCode).IsEqualTo(400);
    }

    private static WebServer CreateServer(int port)
    {
        var container = new SvcContainer();
        container.Build();
        var app = new WebApp(container);
        EndpointRegistrar.RegisterAll(app);

        return new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
    }
}
