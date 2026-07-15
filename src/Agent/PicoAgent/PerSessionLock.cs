using System.Collections.Concurrent;

namespace PicoAgent;

/// <summary>
/// Per-session semaphore. Different session IDs get independent locks;
/// the same session ID is serialized. Semaphores are lazily created.
/// Call <see cref="Remove"/> when a session is deleted to release resources.
/// </summary>
public sealed class PerSessionLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid sessionId, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    public void Remove(Guid sessionId)
    {
        if (_locks.TryRemove(sessionId, out var sem))
            sem.Dispose();
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        public void Dispose() => sem.Release();
    }
}
