namespace PicoWeb.DI.Tests;

/// <summary>
/// The scope the handler sees is a real, per-request <see cref="ISvcScope"/>: a new one for every request, and one
/// instance per scoped service <em>inside</em> a request. Both legs (real container / double) must agree, because a
/// pipeline that reuses a scope across requests would satisfy the service-id tests only by accident.
/// </summary>
public sealed class DIScopeLifecycleTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Scope_identity_differs_per_request(bool useRealContainer)
    {
        var scopes = new ConcurrentBag<ISvcScope>();
        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c => c.RegisterScoped<ScopeMarker>(_ => new ScopeMarker()),
            app =>
                app.MapGet(
                    "/",
                    (ctx, _) =>
                    {
                        // Resolve through the scope before recording it: a scope that cannot serve its own
                        // scoped registration is not the object this test claims to observe.
                        ctx.Services.GetService(typeof(ScopeMarker));
                        scopes.Add(ctx.Services);
                        return ValueTask.FromResult(WebResults.Text(200, "ok"));
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        await client.GetAsync("/");
        await client.GetAsync("/");

        await Assert.That(scopes.Count).IsEqualTo(2);
        await Assert.That(scopes.Distinct(ReferenceEqualityComparer.Instance).Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Scoped_service_is_one_instance_within_a_request(bool useRealContainer)
    {
        var ids = new List<string>();
        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c => c.RegisterScoped<ScopedService>(_ => new ScopedService()),
            app =>
                app.MapGet(
                    "/",
                    (ctx, _) =>
                    {
                        var first = (ScopedService)ctx.Services.GetService(typeof(ScopedService));
                        var second = (ScopedService)ctx.Services.GetService(typeof(ScopedService));
                        ids.Add(first.Id);
                        ids.Add(second.Id);
                        return ValueTask.FromResult(WebResults.Text(200, first.Id));
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ids.Count).IsEqualTo(2);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(1);
    }
}
