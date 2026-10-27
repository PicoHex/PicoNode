using System.Net.Sockets;

namespace PicoWeb.Integration.Tests;

/// <summary>
/// Pins the port rule the rest of the integration suite relies on: a test server is <b>bound</b> to an
/// OS-assigned port and the port it got is read back from the listening socket. The alternative — asking
/// the OS for a free port, closing it, and binding it later — leaves the port owned by nobody in between.
/// </summary>
public sealed class TestSupportTests
{
    private const int ConcurrentServers = 8;

    [Test]
    public async Task Server_binds_port_zero_and_reports_the_port_it_got()
    {
        await using var running = await TestSupport.StartServerAsync(CreateServer);

        await Assert.That(running.Port).IsGreaterThan(0);
        await Assert
            .That(((IPEndPoint)running.Server.LocalEndPoint!).Port)
            .IsEqualTo(running.Port)
            .Because("the reported port must be the bound one, not the one that was asked for");

        // The port is only "the port it got" if a client can reach the server on it.
        using var client = new HttpClient { BaseAddress = running.BaseUri };
        var response = await client.GetAsync("/api/health");
        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("ok");
    }

    [Test]
    public async Task Concurrent_servers_each_get_their_own_bound_port()
    {
        // What the probe-and-bind helper could not promise: eight servers started at the same time. Each
        // one binds when the OS assigns the port, so the ports are distinct by construction rather than
        // by luck, and every one of them is reachable while the others are up.
        var running = await Task.WhenAll(
            Enumerable
                .Range(0, ConcurrentServers)
                .Select(_ => TestSupport.StartServerAsync(CreateServer))
        );

        try
        {
            var ports = running.Select(server => server.Port).ToArray();
            await Assert.That(ports.Distinct().Count()).IsEqualTo(ConcurrentServers);
            await Assert.That(ports).DoesNotContain(0);

            foreach (var server in running)
            {
                using var client = new HttpClient { BaseAddress = server.BaseUri };
                var response = await client.GetAsync("/api/health");
                await Assert
                    .That((int)response.StatusCode)
                    .IsEqualTo(200)
                    .Because($"port {server.Port} was reported as bound but does not answer");
            }
        }
        finally
        {
            foreach (var server in running)
            {
                await server.DisposeAsync();
            }
        }
    }

    private static WebServer CreateServer(int port)
    {
        var container = new SvcContainer();
        container.Build();
        var app = new WebApp(container);
        app.MapGet(
            "/api/health",
            static (WebContext ctx, CancellationToken _) =>
                ValueTask.FromResult(WebResults.Json(200, """{"ok":true}""", "OK"))
        );

        return new WebServer(
            app,
            new WebServerOptions { Endpoint = new IPEndPoint(IPAddress.Loopback, port) }
        );
    }
}
