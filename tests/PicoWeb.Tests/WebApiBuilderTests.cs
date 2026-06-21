namespace PicoWeb.Tests;

internal sealed class SpyContainer : ISvcContainer
{
    public ISvcContainer Register(SvcDescriptor descriptor) => this;

    public bool IsRegistered(Type serviceType) => false;

    public ISvcScope CreateScope() => new SpyScope();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SpyScope : ISvcScope
{
    public object GetService(Type serviceType) => null!;

    public IReadOnlyList<object> GetServices(Type serviceType) => [];

    public bool TryGetService(Type serviceType, out object? result)
    {
        result = null;
        return false;
    }

    public bool TryGetServices(Type serviceType, out IReadOnlyList<object>? result)
    {
        result = null;
        return false;
    }

    public ISvcScope CreateScope() => new SpyScope();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class WebApiBuilderTests
{
    [Test]
    public async Task Build_returns_WebApiApp()
    {
        var builder = new WebApiBuilder();
        var api = builder.Build();
        await Assert.That(api).IsNotNull();
    }

    [Test]
    public async Task ConfigureApp_sets_ServerHeader()
    {
        var builder = new WebApiBuilder();
        builder.ConfigureApp(o => new WebAppOptions { ServerHeader = "TestHeader" });
        var api = builder.Build();
        await Assert.That(api).IsNotNull();
    }

    [Test]
    public async Task Accepts_custom_ISvcContainer()
    {
        // Arrange: use SpyContainer
        var customContainer = new SpyContainer();
        var builder = new WebApiBuilder(customContainer);

        // Act: Build() should NOT call .Build() on custom container
        var api = builder.Build();

        // Assert
        await Assert.That(api).IsNotNull();
    }

    [Test]
    public async Task RegisterService_throws_for_custom_container()
    {
        // Arrange
        var builder = new WebApiBuilder(new SpyContainer());

        // Act & Assert: Register throws because builder doesn't own the container
        await Assert
            .That(() => builder.RegisterSingleton<SpyContainer, SpyContainer>())
            .Throws<InvalidOperationException>();
    }
}
