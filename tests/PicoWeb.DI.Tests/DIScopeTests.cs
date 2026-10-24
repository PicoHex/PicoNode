namespace PicoWeb.DI.Tests;

/// <summary>
/// Container-level scope contract, no HTTP in the way: a scope releases what it created, exactly once, and gives it
/// up on both disposal paths (sync <see cref="IDisposable"/> and async <see cref="IAsyncDisposable"/>).
/// <para>
/// Run against the real container this pins PicoDI's behaviour; run against the double it pins the double. Before
/// this test existed the double tracked only <see cref="IDisposable"/>, so an async-disposable scoped service was
/// never released and every test still passed.
/// </para>
/// </summary>
public sealed class DIScopeTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Scope_releases_scoped_resources_exactly_once(bool useRealContainer)
    {
        var ledger = new DisposalLedger();
        await using var container = TestHost.BuildContainer(
            useRealContainer,
            c => Probes.RegisterScoped(c, ledger)
        );

        var scope = container.CreateScope();
        Probes.ResolveAll(scope);

        await Assert.That(ledger.Created).IsEqualTo(2);
        await Assert.That(ledger.Released).IsEqualTo(0);

        await scope.DisposeAsync();

        await Assert.That(ledger.Released).IsEqualTo(2);
        await Assert.That(ledger.DisposalsPerScope).IsEqualTo("1,1");
        await Assert.That(ledger.Kinds).IsEqualTo("IAsyncDisposable+IDisposable");

        // A second disposal of the same scope must not release anything again: a double release of a pooled or
        // shared resource is as wrong as a leak, and the counts have to say so.
        await scope.DisposeAsync();

        await Assert.That(ledger.MaxDisposals).IsEqualTo(1);
        await Assert.That(ledger.DisposalsPerScope).IsEqualTo("1,1");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Container_release_reaches_scopes_it_handed_out(bool useRealContainer)
    {
        var ledger = new DisposalLedger();
        var container = TestHost.BuildContainer(
            useRealContainer,
            c => Probes.RegisterScoped(c, ledger)
        );

        Probes.ResolveAll(container.CreateScope());
        await Assert.That(ledger.Released).IsEqualTo(0);

        await container.DisposeAsync();

        await Assert.That(ledger.Released).IsEqualTo(2);
        await Assert.That(ledger.MaxDisposals).IsEqualTo(1);
    }
}
