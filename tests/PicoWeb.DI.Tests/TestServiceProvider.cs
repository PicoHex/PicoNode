namespace PicoWeb.DI.Tests;

/// <summary>
/// Minimal <see cref="ISvcContainer"/> double — it covers <b>pipeline wiring only</b>.
/// <para>
/// It exists so the web tests can run without a DI package, and so a wiring failure (a handler not receiving a
/// scope, a middleware dropping <c>ctx.Services</c>) is attributable to the pipeline rather than to PicoDI.
/// It is <b>not</b> a model of PicoDI: it has no freezing, no generated-factory requirement, no
/// LIFO/child-scope disposal ordering, no singleton sealing, no <c>SingleInstance</c> ownership rules, and it
/// resolves one registration per service type. Every lifetime or disposal claim therefore also runs against the
/// real <c>SvcContainer</c> (<c>TestHost.BuildApp(useRealContainer: true, …)</c>); a claim that only the double
/// satisfies is a bug in the double.
/// </para>
/// </summary>
internal sealed class TestServiceProvider : ISvcContainer
{
    private readonly Dictionary<Type, ServiceRegistration> _registrations = new();
    private readonly Dictionary<Type, object> _singletonInstances = new();
    private readonly List<TestServiceScope> _openScopes = new();
    private readonly object _lock = new();
    private TestServiceScope? _rootScope;

    public ISvcContainer Register(SvcDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // The real container can satisfy a type-based registration through PicoDI.Gen; the double cannot, and
        // silently falling back to Activator.CreateInstance would be both trim-unsafe (IL2072) and a behaviour
        // the double is not modelling. Fail loudly instead.
        var factory =
            descriptor.Factory
            ?? throw new NotSupportedException(
                $"TestServiceProvider only resolves explicit factories (no factory for {descriptor.ServiceType})."
            );

        var lifetime = descriptor.Lifetime switch
        {
            SvcLifetime.Singleton => FakeLifetime.Singleton,
            SvcLifetime.Scoped => FakeLifetime.Scoped,
            SvcLifetime.Transient => FakeLifetime.Transient,
            _ => throw new NotSupportedException($"Unmapped lifetime {descriptor.Lifetime}."),
        };

        _registrations[descriptor.ServiceType] = new ServiceRegistration(lifetime, factory);
        return this;
    }

    public bool IsRegistered(Type serviceType) => _registrations.ContainsKey(serviceType);

    public ISvcScope CreateScope()
    {
        var scope = new TestServiceScope(this);
        lock (_lock)
            _openScopes.Add(scope);
        return scope;
    }

    public async ValueTask DisposeAsync()
    {
        // A real container reclaims the scopes it handed out when it is itself disposed; a request scope that
        // leaks must at least be released at shutdown. The double tracked nothing, so it could not tell a leak
        // from a clean shutdown (measured: released=0 vs the real container's 2).
        TestServiceScope[] open;
        lock (_lock)
        {
            open = [.. _openScopes];
            _openScopes.Clear();
        }

        foreach (var scope in open)
            await scope.DisposeAsync().ConfigureAwait(false);

        foreach (var instance in _singletonInstances.Values)
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        _singletonInstances.Clear();
        _registrations.Clear();
    }

    internal object? Resolve(Type serviceType, TestServiceScope scope)
    {
        if (!_registrations.TryGetValue(serviceType, out var registration))
            return null;

        return registration.Lifetime switch
        {
            FakeLifetime.Singleton => GetOrCreateSingleton(serviceType, registration),
            FakeLifetime.Scoped => scope.GetOrCreateScoped(serviceType, registration),
            FakeLifetime.Transient => registration.Factory(scope),
            _ => null,
        };
    }

    internal void ForgetScope(TestServiceScope scope)
    {
        lock (_lock)
            _openScopes.Remove(scope);
    }

    private object GetOrCreateSingleton(Type serviceType, ServiceRegistration registration)
    {
        if (_singletonInstances.TryGetValue(serviceType, out var instance))
            return instance;

        lock (_lock)
        {
            if (_singletonInstances.TryGetValue(serviceType, out instance))
                return instance;

            // Feed singleton factories a container-owned root scope, never the (short-lived) resolving scope:
            // that is the documented PicoDI contract ("singletons never capture a possibly short-lived resolving
            // scope") and the double used to violate it.
            _rootScope ??= new TestServiceScope(this);
            instance = registration.Factory(_rootScope);
            _singletonInstances[serviceType] = instance;
            return instance;
        }
    }
}

internal sealed class TestServiceScope : ISvcScope
{
    private readonly TestServiceProvider _provider;
    private readonly Dictionary<Type, object> _scopedInstances = new();
    private readonly List<object> _trackedDisposables = new();

    public TestServiceScope(TestServiceProvider provider)
    {
        _provider = provider;
    }

    public object GetService(Type serviceType) => _provider.Resolve(serviceType, this)!;

    public IReadOnlyList<object> GetServices(Type serviceType)
    {
        var result = GetService(serviceType);
        return result is not null ? [result] : [];
    }

    public bool TryGetService(Type serviceType, [NotNullWhen(true)] out object? result)
    {
        result = _provider.Resolve(serviceType, this);
        return result is not null;
    }

    public bool TryGetServices(
        Type serviceType,
        [NotNullWhen(true)] out IReadOnlyList<object>? result
    )
    {
        var r = GetService(serviceType);
        result = r is not null ? [r] : null;
        return result is not null;
    }

    public ISvcScope CreateScope() => _provider.CreateScope();

    internal object? GetOrCreateScoped(Type serviceType, ServiceRegistration registration)
    {
        if (_scopedInstances.TryGetValue(serviceType, out var instance))
            return instance;

        instance = registration.Factory(this);
        _scopedInstances[serviceType] = instance;

        // Both disposal contracts must be tracked. Tracking only IDisposable (as this double used to) hides a
        // real container behaviour — and the leak it would allow — from every test that runs on the double.
        if (instance is IDisposable || instance is IAsyncDisposable)
            _trackedDisposables.Add(instance);

        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        var tracked = _trackedDisposables.ToArray();
        _trackedDisposables.Clear();
        _scopedInstances.Clear();
        _provider.ForgetScope(this);

        foreach (var instance in tracked)
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}

internal enum FakeLifetime
{
    Singleton,
    Scoped,
    Transient,
}

internal sealed record ServiceRegistration(FakeLifetime Lifetime, Func<ISvcScope, object> Factory);
