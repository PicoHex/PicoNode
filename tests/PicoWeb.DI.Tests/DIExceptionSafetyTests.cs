namespace PicoWeb.DI.Tests;

/// <summary>
/// Exception safety of the request scope. A failed request must still release the scope it created — exactly once,
/// and without poisoning the next request on the connection.
/// </summary>
public sealed class DIExceptionSafetyTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Scope_released_once_when_the_handler_throws(bool useRealContainer)
    {
        var ledger = new DisposalLedger();
        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c => Probes.RegisterScoped(c, ledger),
            app =>
                app.MapGet(
                    "/",
                    (ctx, _) =>
                    {
                        Probes.ResolveAll(ctx.Services);
                        throw new InvalidOperationException("boom");
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(ledger.Created).IsEqualTo(2);
        await Assert.That(ledger.Released).IsEqualTo(2);
        await Assert.That(ledger.DisposalsPerScope).IsEqualTo("1,1");
        await Assert.That(ledger.Kinds).IsEqualTo("IAsyncDisposable+IDisposable");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Exception_in_one_request_does_not_leak_or_break_the_next(
        bool useRealContainer
    )
    {
        var ledger = new DisposalLedger();
        var failing = true;
        var (container, app) = TestHost.BuildApp(
            useRealContainer,
            c => Probes.RegisterScoped(c, ledger),
            app =>
                app.MapGet(
                    "/",
                    (ctx, _) =>
                    {
                        Probes.ResolveAll(ctx.Services);
                        if (failing)
                            throw new InvalidOperationException("boom");
                        return ValueTask.FromResult(WebResults.Text(200, "ok"));
                    }
                )
        );

        await using var ownedContainer = container;
        await using var host = await TestWebHost.StartAsync(app);
        using var client = host.CreateClient();

        var failed = await client.GetAsync("/");
        failing = false;
        var succeeded = await client.GetAsync("/");

        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(succeeded.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Two requests, two scopes, each released exactly once: neither request inherited the other's scope.
        await Assert.That(ledger.Created).IsEqualTo(4);
        await Assert.That(ledger.DisposalsPerScope).IsEqualTo("1,1,1,1");
    }
}
