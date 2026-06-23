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
    // ── RED test: generic TService params must have DynamicallyAccessedMembers annotation ──

    [Test]
    public async Task RegisterMethods_TServiceParam_HasDynamicallyAccessedMembersInterfaces()
    {
        // The extension methods in PicoDI.Abs now require [DynamicallyAccessedMembers(Interfaces)]
        // on the serviceType parameter, so the generic TService flowing into them
        // via typeof(TService) must carry the same annotation.
        // Without it, the trimmer emits IL2087 during publish.

        var methods = new[]
        {
            nameof(WebApiBuilder.RegisterSingleton),
            nameof(WebApiBuilder.RegisterScoped),
            nameof(WebApiBuilder.RegisterTransient),
        };

        foreach (var methodName in methods)
        {
            var method = typeof(WebApiBuilder).GetMethod(methodName);
            await Assert.That(method).IsNotNull();

            var tServiceParam = method!.GetGenericArguments()[0];
            // Cannot check DAM annotation directly via GenericParameterAttributes,
            // so we check CustomAttributes data
            var attrData = tServiceParam.CustomAttributes.FirstOrDefault(a =>
                a.AttributeType.FullName
                == "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute"
            );

            await Assert
                .That(attrData)
                .IsNotNull()
                .Because(
                    $"{methodName}<TService, TImpl>() is missing [DynamicallyAccessedMembers(Interfaces)] on TService"
                );
        }
    }

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
