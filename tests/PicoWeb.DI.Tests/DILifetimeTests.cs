namespace PicoWeb.DI.Tests;

public sealed class DILifetimeTests
{
    [Test]
    public async Task Singleton_survives_scope_disposal()
    {
        var ids = new ConcurrentBag<string>();
        var app = new WebApp();
        app.MapGet("/", (ctx, _) =>
        {
            var svc = ctx.Services!.GetService(typeof(RequestIdService)) as RequestIdService;
            ids.Add(svc!.Id);
            return ValueTask.FromResult(WebResults.Text(200, svc.Id));
        });

        await using var container = new TestServiceProvider();
        container.RegisterSingleton(typeof(RequestIdService), _ => new RequestIdService());
        container.Build();

        await using var host = await TestWebHost.StartAsync(app, container);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };

        await client.GetAsync("/");
        await client.GetAsync("/");
        await client.GetAsync("/");

        await Assert.That(ids.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Scoped_creates_new_instance_per_request()
    {
        var ids = new ConcurrentBag<string>();
        var app = new WebApp();
        app.MapGet("/", (ctx, _) =>
        {
            var svc = ctx.Services!.GetService(typeof(RequestIdService)) as RequestIdService;
            ids.Add(svc!.Id);
            return ValueTask.FromResult(WebResults.Text(200, "ok"));
        });

        await using var container = new TestServiceProvider();
        container.RegisterScoped(typeof(RequestIdService), _ => new RequestIdService());
        container.Build();

        await using var host = await TestWebHost.StartAsync(app, container);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };

        await client.GetAsync("/");
        await client.GetAsync("/");
        await client.GetAsync("/");

        await Assert.That(ids.Distinct().Count()).IsEqualTo(3);
    }
}
