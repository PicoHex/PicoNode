namespace PicoWeb.DI.Tests;

internal sealed class TestServiceProvider : PicoNode.Abs.IServiceProvider, IAsyncDisposable
{
    private readonly Dictionary<Type, ServiceRegistration> _registrations = new();
    private readonly Dictionary<Type, object> _singletonInstances = new();
    private readonly object _lock = new();

    public void RegisterSingleton(Type serviceType, Func<PicoNode.Abs.IServiceScope, object> factory)
    {
        _registrations[serviceType] = new ServiceRegistration(ServiceLifetime.Singleton, factory);
    }

    public void RegisterScoped(Type serviceType, Func<PicoNode.Abs.IServiceScope, object> factory)
    {
        _registrations[serviceType] = new ServiceRegistration(ServiceLifetime.Scoped, factory);
    }

    public void RegisterTransient(Type serviceType, Func<PicoNode.Abs.IServiceScope, object> factory)
    {
        _registrations[serviceType] = new ServiceRegistration(ServiceLifetime.Transient, factory);
    }

    public void Build()
    {
        // no-op for compatibility with SvcContainer API
    }

    public PicoNode.Abs.IServiceScope CreateScope()
    {
        return new TestServiceScope(this);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var instance in _singletonInstances.Values)
        {
            if (instance is IDisposable d)
                d.Dispose();
        }
        _singletonInstances.Clear();
        _registrations.Clear();
        return ValueTask.CompletedTask;
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

    private object GetOrCreateSingleton(Type serviceType, ServiceRegistration registration, TestServiceScope scope)
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

internal sealed class TestServiceScope : PicoNode.Abs.IServiceScope
{
    private readonly TestServiceProvider _provider;
    private readonly Dictionary<Type, object> _scopedInstances = new();
    private readonly List<IDisposable> _disposables = new();

    public TestServiceScope(TestServiceProvider provider)
    {
        _provider = provider;
    }

    public object? GetService(Type serviceType)
    {
        return _provider.Resolve(serviceType, this);
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
}

internal enum ServiceLifetime
{
    Singleton,
    Scoped,
    Transient,
}

internal sealed record ServiceRegistration(ServiceLifetime Lifetime, Func<PicoNode.Abs.IServiceScope, object> Factory);
