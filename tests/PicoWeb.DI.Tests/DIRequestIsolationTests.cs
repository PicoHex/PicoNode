namespace PicoWeb.DI.Tests;

public sealed class DIRequestIsolationTests
{
    [Test]
    public async Task Each_request_gets_independent_scope()
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
        container.RegisterScoped(typeof(RequestIdService), _ => new RequestIdService());
        container.Build();

        await using var host = await TestWebHost.StartAsync(app, container);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };

        await client.GetAsync("/");
        await client.GetAsync("/");
        await client.GetAsync("/");

        await Assert.That(ids.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task Singleton_same_across_requests()
    {
        var app = new WebApp();
        app.MapGet("/", (ctx, _) =>
        {
            var counter = ctx.Services!.GetService(typeof(SingletonCounter)) as SingletonCounter;
            return ValueTask.FromResult(WebResults.Text(200, counter!.Increment().ToString()));
        });

        await using var container = new TestServiceProvider();
        container.RegisterSingleton(typeof(SingletonCounter), _ => new SingletonCounter());
        container.Build();

        await using var host = await TestWebHost.StartAsync(app, container);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };

        await client.GetAsync("/");
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(body).IsEqualTo("2");
    }
}
