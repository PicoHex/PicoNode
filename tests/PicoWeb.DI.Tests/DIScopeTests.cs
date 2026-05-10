namespace PicoWeb.DI.Tests;

public sealed class DIScopeTests
{
    [Test]
    public async Task ServiceProvider_creates_and_disposes_scope()
    {
        await using var container = new TestServiceProvider();
        await using var scope = container.CreateScope();

        await Assert.That(scope).IsNotNull();

        await scope.DisposeAsync();
    }
}
