namespace PicoWeb.DI.Tests;

internal sealed class TestServiceProvider : ISvcContainer
{
    private readonly Dictionary<Type, ServiceRegistration> _registrations = new();
    private readonly Dictionary<Type, object> _singletonInstances = new();
    private readonly object _lock = new();

    public void RegisterSingleton(
        Type serviceType,
        Func<ISvcScope, object> factory
    )
    {
        _registrations[serviceType] = new ServiceRegistration(ServiceLifetime.Singleton, factory);
    }

    public void RegisterScoped(
        Type serviceType,
        Func<ISvcScope, object> factory
    )
    {
        _registrations[serviceType] = new ServiceRegistration(ServiceLifetime.Scoped, factory);
    }

    public void RegisterTransient(
        Type serviceType,
        Func<ISvcScope, object> factory
    )
    {
        _registrations[serviceType] = new ServiceRegistration(ServiceLifetime.Transient, factory);
    }

    public ISvcContainer Register(SvcDescriptor descriptor)
    {
        var factory = descriptor.Factory ?? (_ => Activator.CreateInstance(descriptor.ImplementationType)!);
        var lifetime = descriptor.Lifetime switch
        {
            SvcLifetime.Singleton => ServiceLifetime.Singleton,
            SvcLifetime.Scoped => ServiceLifetime.Scoped,
            SvcLifetime.Transient => ServiceLifetime.Transient,
            _ => ServiceLifetime.Transient,
        };
        _registrations[descriptor.ServiceType] = new ServiceRegistration(lifetime, factory);
        return this;
    }

    public void Build()
    {
        // no-op for compatibility with SvcContainer API
    }

    public ISvcScope CreateScope()
    {
        return new TestServiceScope(this);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _singletonInstances.Values)
        {
            if (instance is IDisposable d)
                d.Dispose();
        }
        _singletonInstances.Clear();
        _registrations.Clear();
        await ValueTask.CompletedTask;
    }

    internal object? Resolve(Type serviceType, TestServiceScope scope)
    {
        if (!_registrations.TryGetValue(serviceType, out var registration))
            return null;

        return registration.Lifetime switch
        {
            ServiceLifetime.Singleton => GetOrCreateSingleton(serviceType, registration, scope),
            ServiceLifetime.Scoped => scope.GetOrCreateScoped(serviceType, registration),
            ServiceLifetime.Transient => registration.Factory(scope),
            _ => null,
        };
    }

    private object GetOrCreateSingleton(
        Type serviceType,
        ServiceRegistration registration,
        TestServiceScope scope
    )
    {
        if (_singletonInstances.TryGetValue(serviceType, out var instance))
            return instance;

        lock (_lock)
        {
            if (_singletonInstances.TryGetValue(serviceType, out instance))
                return instance;

            instance = registration.Factory(scope);
            _singletonInstances[serviceType] = instance;
            return instance;
        }
    }
}

internal sealed class TestServiceScope : ISvcScope
{
    private readonly TestServiceProvider _provider;
    private readonly Dictionary<Type, object> _scopedInstances = new();
    private readonly List<IDisposable> _disposables = new();

    public TestServiceScope(TestServiceProvider provider)
    {
        _provider = provider;
    }

    public object GetService(Type serviceType)
    {
        return _provider.Resolve(serviceType, this)!;
    }

    public IReadOnlyList<object> GetServices(Type serviceType)
    {
        var result = GetService(serviceType);
        return result is not null ? new[] { result } : Array.Empty<object>();
    }

    public ISvcScope CreateScope()
    {
        return _provider.CreateScope();
    }

    internal object? GetOrCreateScoped(Type serviceType, ServiceRegistration registration)
    {
        if (_scopedInstances.TryGetValue(serviceType, out var instance))
            return instance;

        instance = registration.Factory(this);
        _scopedInstances[serviceType] = instance;
        if (instance is IDisposable d)
            _disposables.Add(d);
        return instance;
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        _disposables.Clear();
        _scopedInstances.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await ValueTask.CompletedTask;
    }
}

internal enum ServiceLifetime
{
    Singleton,
    Scoped,
    Transient,
}

internal sealed record ServiceRegistration(
    ServiceLifetime Lifetime,
    Func<ISvcScope, object> Factory
);
