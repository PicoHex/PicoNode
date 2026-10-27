namespace PicoWeb.DI.Tests;

/// <summary>
/// The lifetime matrix — singleton, scoped, transient — resolved by handlers through a request scope.
/// <para>
/// Every case runs twice: <c>useRealContainer: true</c> against PicoDI's <see cref="SvcContainer"/>, and
/// <c>false</c> against <see cref="TestServiceProvider"/>. The real leg is the one that can prove a lifetime claim;
/// the double leg only proves the pipeline wiring. When the two disagree the double is wrong, not the container.
/// </para>
/// </summary>
public sealed class DILifetimeTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Singleton_is_shared_across_requests(bool useRealContainer)
    {
        var ids = new ConcurrentBag<string>();
        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c => c.RegisterSingleton<RequestIdService>(_ => new RequestIdService()),
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

        await Assert.That(ids.Count).IsEqualTo(3);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Scoped_creates_new_instance_per_request(bool useRealContainer)
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
                        return ValueTask.FromResult(WebResults.Text(200, "ok"));
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        await client.GetAsync("/");
        await client.GetAsync("/");
        await client.GetAsync("/");

        await Assert.That(ids.Count).IsEqualTo(3);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Transient_creates_new_instance_per_resolution(bool useRealContainer)
    {
        var ids = new List<string>();
        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c => c.Register<RequestIdService>(_ => new RequestIdService(), SvcLifetime.Transient),
            app =>
                app.MapGet(
                    "/",
                    (ctx, _) =>
                    {
                        // Two resolutions inside ONE scope: a transient must differ, a scoped must not.
                        var first = (RequestIdService)
                            ctx.Services.GetService(typeof(RequestIdService));
                        var second = (RequestIdService)
                            ctx.Services.GetService(typeof(RequestIdService));
                        ids.Add(first.Id);
                        ids.Add(second.Id);
                        return ValueTask.FromResult(WebResults.Text(200, "ok"));
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ids.Count).IsEqualTo(2);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(2);
    }
}
