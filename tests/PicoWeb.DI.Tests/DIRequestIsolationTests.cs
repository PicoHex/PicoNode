namespace PicoWeb.DI.Tests;

/// <summary>
/// Request isolation, measured on both containers. The interesting case is the singleton: PicoDI invokes a
/// singleton factory on a container-owned root scope so the singleton cannot capture a request scope that dies
/// with the request. The double used to hand the factory the resolving scope — the exact opposite — and no test
/// could tell, because every test asked the double.
/// </summary>
public sealed class DIRequestIsolationTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Each_request_gets_independent_scope(bool useRealContainer)
    {
        var ids = new ConcurrentBag<string>();
        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c => c.RegisterScoped<RequestIdService>(_ => new RequestIdService()),
            app =>
                app.MapGet(
                    "/",
                    (ctx, _) =>
                    {
                        var svc = (RequestIdService)
                            ctx.Services.GetService(typeof(RequestIdService));
                        ids.Add(svc.Id);
                        return ValueTask.FromResult(WebResults.Text(200, svc.Id));
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        await client.GetAsync("/");
        await client.GetAsync("/");
        await client.GetAsync("/");

        await Assert.That(ids.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Singleton_state_survives_between_requests(bool useRealContainer)
    {
        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c => c.RegisterSingleton<SingletonCounter>(_ => new SingletonCounter()),
            app =>
                app.MapGet(
                    "/",
                    (ctx, _) =>
                    {
                        var counter = (SingletonCounter)
                            ctx.Services.GetService(typeof(SingletonCounter));
                        return ValueTask.FromResult(
                            WebResults.Text(
                                200,
                                counter!.Increment().ToString(CultureInfo.InvariantCulture)
                            )
                        );
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        await client.GetAsync("/");
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(body).IsEqualTo("2");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Singleton_factory_does_not_capture_the_request_scope(bool useRealContainer)
    {
        ISvcScope? factoryScope = null;
        ISvcScope? requestScope = null;

        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c =>
                c.RegisterSingleton<SingletonCounter>(scope =>
                {
                    factoryScope = scope;
                    return new SingletonCounter();
                }),
            app =>
                app.MapGet(
                    "/",
                    (ctx, _) =>
                    {
                        requestScope = ctx.Services;
                        ctx.Services.GetService(typeof(SingletonCounter));
                        return ValueTask.FromResult(WebResults.Text(200, "ok"));
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        await client.GetAsync("/");

        await Assert.That(requestScope).IsNotNull();
        await Assert.That(factoryScope).IsNotNull();
        await Assert.That(ReferenceEquals(factoryScope, requestScope)).IsFalse();
    }
}
