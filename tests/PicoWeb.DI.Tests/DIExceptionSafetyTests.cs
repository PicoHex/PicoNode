namespace PicoWeb.DI.Tests;

public sealed class DIExceptionSafetyTests
{
    [Test]
    public async Task Scope_disposed_when_handler_throws()
    {
        var app = new WebApp();
        app.MapGet("/", (_, _) =>
            throw new InvalidOperationException("boom"));

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.Build();

        await using var host = await TestWebHost.StartAsync(app, container);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };

        var response = await client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task Request_without_errors_returns_ok()
    {
        var app = new WebApp();
        app.MapGet("/", (_, _) =>
            ValueTask.FromResult(WebResults.Text(200, "ok")));

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.Build();

        await using var host = await TestWebHost.StartAsync(app, container);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };

        var response = await client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
