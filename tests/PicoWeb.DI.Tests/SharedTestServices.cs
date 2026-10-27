namespace PicoWeb.DI.Tests;

public sealed class ScopeMarker;

/// <summary>
/// Per-test accounting of the disposable resources a DI scope created.
/// <para>
/// The suite used to assert only that the container <em>resolved</em> a service. Nothing observed whether the
/// request scope actually <em>released</em> what it created, so a scope that leaked every scoped service still
/// produced a green run. A probe registers itself here on construction and records every <c>Dispose</c>/
/// <c>DisposeAsync</c>, which makes "released exactly once per scope" a countable fact instead of a hope.
/// </para>
/// <para>
/// One row per probe instance. Because probes are registered <c>Scoped</c>, one row is one scope, so
/// <see cref="DisposalsPerScope"/> reads like a per-request receipt (for example <c>"1,1,1"</c> for three requests).
/// </para>
/// </summary>
public sealed class DisposalLedger
{
    private readonly ConcurrentDictionary<int, ProbeRecord> _records = new();
    private int _nextOrder;

    /// <summary>
    /// Set by the streaming tests: reads <see langword="true"/> once the response body producer has signalled EOF.
    /// </summary>
    public Func<bool> BodyReachedEof { get; set; } = static () => false;

    /// <summary>
    /// Set by the streaming tests: reads <see langword="true"/> once the response body producer stream has been
    /// disposed. <c>ScopeBoundStream</c> disposes the producer <em>before</em> it releases the scope, so a probe
    /// released while this is already true came from the stream-disposal route and not from the EOF route.
    /// </summary>
    public Func<bool> BodyStreamDisposed { get; set; } = static () => false;

    /// <summary>Number of probes built, i.e. the number of scopes that resolved a probe.</summary>
    public int Created => _records.Count;

    /// <summary>Number of probes that were disposed at least once.</summary>
    public int Released => _records.Values.Count(static r => Volatile.Read(ref r.Disposals) > 0);

    /// <summary>Highest disposal count any single probe reached. 2 means a double release.</summary>
    public int MaxDisposals =>
        _records.IsEmpty ? 0 : _records.Values.Max(static r => Volatile.Read(ref r.Disposals));

    /// <summary>
    /// Probes released from inside the final body read: the producer had reported EOF and had <b>not</b> been
    /// disposed yet. That is <c>ScopeBoundStream.ReadAsync</c>'s release, and nothing else.
    /// </summary>
    public int ReleasedInsideFinalBodyRead =>
        _records.Values.Count(static r => r.EofAtRelease && !r.ProducerDisposedAtRelease);

    /// <summary>
    /// Probes released by disposing the body stream (<c>ScopeBoundStream.DisposeAsync</c>), i.e. a request whose
    /// body was never read to the end.
    /// </summary>
    public int ReleasedByBodyStreamDisposal =>
        _records.Values.Count(static r => r.ProducerDisposedAtRelease);

    /// <summary>Disposal counts in scope-creation order, for example <c>"1,1,1"</c>.</summary>
    public string DisposalsPerScope =>
        string.Join(
            ",",
            _records
                .OrderBy(static r => r.Key)
                .Select(static r => Volatile.Read(ref r.Value.Disposals))
                .Select(static n => n.ToString(CultureInfo.InvariantCulture))
        );

    /// <summary>
    /// Distinct resource kinds that were released exactly once, ordinal-sorted, for example
    /// <c>"IAsyncDisposable+IDisposable"</c>. Two release paths "release the same things" exactly when this
    /// string is equal for both.
    /// </summary>
    public string Kinds =>
        string.Join(
            "+",
            _records
                .Values.Where(static r => Volatile.Read(ref r.Disposals) == 1)
                .Select(static r => r.Kind)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        );

    internal int Register(string kind)
    {
        var order = Interlocked.Increment(ref _nextOrder);
        _records[order] = new ProbeRecord(kind);
        return order;
    }

    internal void RecordDisposal(int order)
    {
        if (!_records.TryGetValue(order, out var record))
            return;

        // Snapshot first, publish last: the Interlocked.Increment is the release fence that makes the two flags
        // visible to the test thread that observes the incremented count.
        record.EofAtRelease = BodyReachedEof();
        record.ProducerDisposedAtRelease = BodyStreamDisposed();
        Interlocked.Increment(ref record.Disposals);
    }
}

internal sealed class ProbeRecord(string kind)
{
    public string Kind { get; } = kind;
    public int Disposals;
    public bool EofAtRelease;
    public bool ProducerDisposedAtRelease;
}

/// <summary>A scoped resource claimed to be released through the synchronous <see cref="IDisposable"/> path.</summary>
public sealed class DisposalProbe : IDisposable
{
    private readonly DisposalLedger _ledger;
    private readonly int _order;

    public DisposalProbe(DisposalLedger ledger)
    {
        _ledger = ledger;
        _order = ledger.Register("IDisposable");
    }

    public void Dispose() => _ledger.RecordDisposal(_order);
}

/// <summary>
/// A scoped resource claimed to be released through the asynchronous <see cref="IAsyncDisposable"/> path.
/// A container that only tracks <see cref="IDisposable"/> silently drops this one, so the two kinds together
/// are what proves a scope "released the same things" on both paths.
/// </summary>
public sealed class AsyncDisposalProbe : IAsyncDisposable
{
    private readonly DisposalLedger _ledger;
    private readonly int _order;

    public AsyncDisposalProbe(DisposalLedger ledger)
    {
        _ledger = ledger;
        _order = ledger.Register("IAsyncDisposable");
    }

    public ValueTask DisposeAsync()
    {
        _ledger.RecordDisposal(_order);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Registration/resolution helpers shared by the disposal-path tests.</summary>
internal static class Probes
{
    public static void RegisterScoped(ISvcContainer container, DisposalLedger ledger)
    {
        container.RegisterScoped<DisposalProbe>(_ => new DisposalProbe(ledger));
        container.RegisterScoped<AsyncDisposalProbe>(_ => new AsyncDisposalProbe(ledger));
    }

    public static void ResolveAll(ISvcScope scope)
    {
        scope.GetService(typeof(DisposalProbe));
        scope.GetService(typeof(AsyncDisposalProbe));
    }
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
