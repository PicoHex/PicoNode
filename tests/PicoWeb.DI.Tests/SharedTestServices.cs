namespace PicoWeb.DI.Tests;

public sealed class ScopeMarker;

public sealed class DisposableSpy : IDisposable
{
    public Action? OnDisposed { get; set; }

    public void Dispose() => OnDisposed?.Invoke();
}

public sealed class ScopedService
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
}

public sealed class SingletonCounter
{
    private int _value;

    public int Increment() => Interlocked.Increment(ref _value);

    public int Value => Volatile.Read(ref _value);
}

public sealed class RequestIdService
{
    public string Id { get; } = Guid.NewGuid().ToString();
}
