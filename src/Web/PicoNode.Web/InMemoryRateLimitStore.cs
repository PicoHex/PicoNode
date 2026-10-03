namespace PicoNode.Web;

public sealed class InMemoryRateLimitStore : IRateLimitStore, IDisposable
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly Timer _cleanupTimer;
    private readonly int _maxTokens;
    private readonly int _refillRate;
    private readonly double _refillIntervalTicks;
    private readonly long _cleanupIntervalTicks;
    private int _disposed;

    /// <summary>
    /// Clock used for refill math. Internal test seam: deterministic tests
    /// advance a manual clock instead of sleeping across the refill interval
    /// (a 50 ms window made "immediate retry is denied" load-sensitive).
    /// </summary>
    /// <remarks>
    /// The consume path reads it while holding that bucket's lock, so a custom provider must be
    /// cheap and must not re-enter this store or wait on anything an in-flight consume could
    /// hold — a test clock that blocks is deliberately used to probe that lock
    /// (InMemoryRateLimitStoreConcurrencyTests). The cleanup sweep reads it once per pass,
    /// outside any bucket lock. Production uses TimeProvider.System.
    /// </remarks>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Retry-After/Reset timestamp when RefillRate=0 (fixed window, 1 year).</summary>
    private const long NoRefillRetryAfterSeconds = 31_536_000;

    /// <summary>Builds a store from a budget. <paramref name="cleanupInterval"/> defaults to 5 minutes.</summary>
    public InMemoryRateLimitStore(in RateLimitBudget budget, TimeSpan? cleanupInterval = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget.MaxTokens, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget.RefillInterval, TimeSpan.Zero);
        // RefillRate=0 is legal (fixed window, never refills) — the refill math
        // below guards against it explicitly. A negative rate is equally "no
        // refill" on the legacy path, so it must not be rejected here.
        // CleanupInterval=0 makes Timer fire exactly once — buckets would
        // never be reclaimed (unbounded growth under key-spray attacks).
        var cleanup = cleanupInterval ?? TimeSpan.FromMinutes(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cleanup, TimeSpan.Zero);

        _maxTokens = budget.MaxTokens;
        _refillRate = budget.RefillRate;
        _refillIntervalTicks = budget.RefillInterval.Ticks;
        _cleanupIntervalTicks = cleanup.Ticks;

        var interval = budget.RefillInterval < cleanup ? budget.RefillInterval : cleanup;

        _cleanupTimer = new Timer(_ => CleanupExpired(), null, interval, interval);
    }

    public InMemoryRateLimitStore(RateLimitOptions options)
        : this(BudgetFrom(options), options?.CleanupInterval) { }

    private static RateLimitBudget BudgetFrom(RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); // preserves the old exception
        return new RateLimitBudget(options.MaxTokens, options.RefillRate, options.RefillInterval);
    }

    public ValueTask<RateLimitResult> TryConsumeTokenAsync(
        string key,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());

        lock (bucket.Lock)
        {
            // Read the clock while holding the lock, so the timestamp the refill math uses
            // is the one the bucket is updated with: read outside, a thread can arrive
            // late holding an older `now` and move LastAccessTicks backwards. (It also
            // makes the internal TimeProvider seam a probe for this lock — a clock that
            // blocks is only ever entered by one thread at a time.)
            var now = TimeProvider.GetUtcNow().Ticks;
            var result = TryConsume(bucket, now);
            return ValueTask.FromResult(result);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cleanupTimer.Dispose();
        _buckets.Clear();
    }

    private RateLimitResult TryConsume(Bucket bucket, long nowTicks)
    {
        // Refill or init
        if (bucket.LastAccessTicks == 0)
        {
            // First access — start full
            bucket.Tokens = _maxTokens;
        }
        else if (_refillRate > 0)
        {
            var elapsed = nowTicks - bucket.LastAccessTicks;
            var tokensToAdd = (elapsed / _refillIntervalTicks) * _refillRate;
            bucket.Tokens = Math.Min(bucket.Tokens + tokensToAdd, _maxTokens);
        }

        bucket.LastAccessTicks = nowTicks;

        // Consume
        var allowed = bucket.Tokens >= 1.0;
        if (allowed)
            bucket.Tokens -= 1.0;

        var remaining = (int)Math.Floor(bucket.Tokens);
        var nowUnix = new DateTimeOffset(nowTicks, TimeSpan.Zero).ToUnixTimeSeconds();

        // NextAvailableAt
        long nextAvailable;
        if (bucket.Tokens >= 1.0)
        {
            nextAvailable = nowUnix;
        }
        else if (_refillRate <= 0)
        {
            // Fixed window: the bucket never refills. A finite far-future
            // timestamp (1 year) — the old code divided by zero here and
            // produced unspecified garbage values.
            nextAvailable = nowUnix + NoRefillRetryAfterSeconds;
        }
        else
        {
            var needed = 1.0 - Math.Max(bucket.Tokens, 0);
            var seconds = (long)
                Math.Ceiling(needed * _refillIntervalTicks / _refillRate / TimeSpan.TicksPerSecond);
            nextAvailable = nowUnix + seconds;
        }

        // ResetAt (when bucket is fully refilled)
        long resetAt;
        if (_refillRate <= 0)
        {
            resetAt = nowUnix + NoRefillRetryAfterSeconds;
        }
        else
        {
            var toFill = _maxTokens - bucket.Tokens;
            var resetSeconds = (long)
                Math.Ceiling(toFill * _refillIntervalTicks / _refillRate / TimeSpan.TicksPerSecond);
            resetAt = nowUnix + resetSeconds;
        }

        return new RateLimitResult
        {
            Allowed = allowed,
            Limit = _maxTokens,
            Remaining = remaining,
            NextAvailableAt = nextAvailable,
            ResetAt = resetAt,
        };
    }

    private void CleanupExpired()
    {
        var cutoff = TimeProvider.GetUtcNow().Ticks - _cleanupIntervalTicks * 2;

        foreach (var (id, bucket) in _buckets)
        {
            // Read under the bucket lock: LastAccessTicks is a plain long written by the
            // consume path (TryConsume), so an unsynchronised read can tear on 32-bit
            // targets and evict a live bucket. The remove stays outside the lock — a
            // consumer that grabs the bucket in between simply gets a fresh allowance,
            // exactly as it would a moment later.
            bool expired;
            lock (bucket.Lock)
            {
                expired = bucket.LastAccessTicks < cutoff;
            }

            if (expired)
                _buckets.TryRemove(id, out _);
        }
    }

    private sealed class Bucket
    {
        public double Tokens;
        public long LastAccessTicks;
        public readonly object Lock = new();
    }
}
