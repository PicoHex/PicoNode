namespace PicoWeb.DI.Tests;

public sealed class DIScopeLifecycleTests
{
    [Test]
    public async Task Scope_created_per_request()
    {
        var container = new TestServiceProvider();
        container.RegisterScoped(typeof(ScopeMarker), _ => new ScopeMarker());

        var capturedScope = new TaskCompletionSource<ISvcScope?>();
        var app = new WebApp(container);
        app.MapGet(
            "/",
            (WebContext ctx, CancellationToken _) =>
            {
                capturedScope.TrySetResult(ctx.Services);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            }
        );

        await using var host = await TestWebHost.StartAsync(app);
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{host.Port}"),
        };

        await client.GetAsync("/");

        await Assert.That(capturedScope.Task.IsCompleted).IsTrue();
    }

    [Test]
    public async Task Scoped_service_resolved_in_handler()
    {
        var container = new TestServiceProvider();
        container.RegisterScoped(typeof(ScopedService), _ => new ScopedService());

        var app = new WebApp(container);
        app.MapGet(
            "/",
            (WebContext ctx, CancellationToken _) =>
            {
                ctx.Services.GetService(typeof(ScopedService));
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            }
        );

        await using var host = await TestWebHost.StartAsync(app);
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{host.Port}"),
        };

        var response = await client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
