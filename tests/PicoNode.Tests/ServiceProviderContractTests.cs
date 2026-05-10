namespace PicoNode.Tests;

public sealed class ServiceProviderContractTests
{
    [Test]
    public async Task Mock_provider_returns_non_null_scope()
    {
        var provider = new MockServiceProvider();
        ISvcScope scope = provider.CreateScope();

        await Assert.That(scope).IsNotNull();
    }

    [Test]
    public async Task GetService_by_type_returns_expected_value()
    {
        var provider = new MockServiceProvider();
        ISvcScope scope = provider.CreateScope();

        object? result = scope.GetService(typeof(string));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.GetType()).IsEqualTo(typeof(string));
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task GetService_generic_extension_works()
    {
        var provider = new MockServiceProvider();
        ISvcScope scope = provider.CreateScope();

        string? result = scope.GetService<string>();

        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task Scope_DisposeAsync_is_callable()
    {
        var scope = (MockServiceScope)new MockServiceProvider().CreateScope();

        await scope.DisposeAsync();

        await Assert.That(scope.Disposed).IsTrue();
    }
}

file sealed class MockServiceProvider : ISvcContainer
{
    public ISvcContainer Register(SvcDescriptor descriptor) => this;

    public ISvcScope CreateScope() => new MockServiceScope();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class MockServiceScope : ISvcScope
{
    public bool Disposed { get; private set; }

    public object GetService(Type serviceType)
    {
        if (serviceType == typeof(string))
            return "hello";
        return null!;
    }

    public IReadOnlyList<object> GetServices(Type serviceType)
    {
        var result = GetService(serviceType);
        return result is not null ? new[] { result } : Array.Empty<object>();
    }

    public ISvcScope CreateScope() => new MockServiceScope();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
