namespace PicoNode.Tests;

public sealed class ServiceProviderContractTests
{
    [Test]
    public async Task Mock_provider_returns_non_null_scope()
    {
        var provider = new MockServiceProvider();
        PicoNode.Web.Abstractions.IServiceScope scope = provider.CreateScope();

        await Assert.That(scope).IsNotNull();
    }

    [Test]
    public async Task GetService_by_type_returns_expected_value()
    {
        var provider = new MockServiceProvider();
        PicoNode.Web.Abstractions.IServiceScope scope = provider.CreateScope();

        object? result = scope.GetService(typeof(string));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.GetType()).IsEqualTo(typeof(string));
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task GetService_generic_extension_works()
    {
        var provider = new MockServiceProvider();
        PicoNode.Web.Abstractions.IServiceScope scope = provider.CreateScope();

        string? result = scope.GetService<string>();

        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task Scope_Dispose_is_callable()
    {
        var scope = (MockServiceScope)new MockServiceProvider().CreateScope();

        scope.Dispose();

        await Assert.That(scope.Disposed).IsTrue();
    }
}

file sealed class MockServiceProvider : PicoNode.Web.Abstractions.IServiceProvider
{
    public PicoNode.Web.Abstractions.IServiceScope CreateScope() => new MockServiceScope();
}

file sealed class MockServiceScope : PicoNode.Web.Abstractions.IServiceScope
{
    public bool Disposed { get; private set; }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(string))
            return "hello";
        return null;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}
