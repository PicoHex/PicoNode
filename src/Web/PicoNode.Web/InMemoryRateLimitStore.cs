namespace PicoNode.Web;

public sealed class InMemoryRateLimitStore : IRateLimitStore, IDisposable
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    /// <summary>
    /// State of buckets whose allowance a fresh bucket would NOT reproduce. A re-created bucket
    /// replays it (see <see cref="AddBucket"/>), so eviction can never refill a bucket.
    /// Bounded by <c>MaxBuckets</c> too (see <see cref="TrimRetired"/>), so the two maps stay
    /// within 2 x the cap instead of trading one growth path for another.
    /// </summary>
    private readonly ConcurrentDictionary<string, RetiredState> _retired = new();
    private readonly Timer _cleanupTimer;
    private readonly int _maxTokens;
    private readonly int _refillRate;
    private readonly double _refillIntervalTicks;
    private readonly long _evictionThresholdTicks;
    private readonly int _maxBuckets;

    /// <summary>
    /// Approximate number of live buckets. <c>ConcurrentDictionary.Count</c> takes every bucket
    /// lock, so the cap check uses this counter instead.
    /// </summary>
    private int _bucketCount;

    /// <summary>Approximate number of carried-forward states (same reason).</summary>
    private int _retiredCount;

    /// <summary>0 = no trimmer running, 1 = one is (see <see cref="EnforceBucketCap"/>).</summary>
    private int _evicting;

    private int _disposed;

    /// <summary>
    /// Store-wide default for <see cref="RateLimitOptions.MaxBuckets"/> and the
    /// <c>maxBuckets</c> constructor parameter. Not public: the number is a documented default,
    /// not a knob callers should pin their own configurations to.
    /// </summary>
    internal const int DefaultMaxBuckets = 100_000;

    /// <summary>
    /// Distinct keys the store is holding buckets for. Test seam only (the cap test asserts the
    /// bound); <c>ConcurrentDictionary.Count</c> takes every bucket lock, so production code
    /// keeps its own counter instead.
    /// </summary>
    internal int BucketCount => _buckets.Count;

    /// <summary>Carried-forward states the store is holding. Test seam only.</summary>
    internal int RetiredCount => _retired.Count;

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
    /// <param name="budget">Token-bucket parameters for every bucket this store keeps.</param>
    /// <param name="cleanupInterval">Cadence of the eviction sweep; defaults to 5 minutes.</param>
    /// <param name="maxBuckets">Upper bound on the number of live buckets; defaults to 100 000.</param>
    public InMemoryRateLimitStore(
        in RateLimitBudget budget,
        TimeSpan? cleanupInterval = null,
        int maxBuckets = DefaultMaxBuckets
    )
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
        // A cap of 0 would evict every key the moment it arrives: the store would answer every
        // request with a fresh, full bucket — the bypass this cap exists to close.
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBuckets, 1);

        _maxTokens = budget.MaxTokens;
        _refillRate = budget.RefillRate;
        _refillIntervalTicks = budget.RefillInterval.Ticks;
        _evictionThresholdTicks = EvictionThreshold(budget.RefillInterval, cleanup);
        _maxBuckets = maxBuckets;

        var interval = budget.RefillInterval < cleanup ? budget.RefillInterval : cleanup;

        _cleanupTimer = new Timer(_ => CleanupExpired(), null, interval, interval);
    }

    public InMemoryRateLimitStore(RateLimitOptions options)
        : this(
            BudgetFrom(options),
            options?.CleanupInterval,
            options?.MaxBuckets ?? DefaultMaxBuckets
        ) { }

    private static RateLimitBudget BudgetFrom(RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); // preserves the old exception
        return new RateLimitBudget(options.MaxTokens, options.RefillRate, options.RefillInterval);
    }

    /// <summary>
    /// How idle a bucket must be before the sweep may reclaim it: twice the longer of the refill
    /// interval and the sweep interval.
    /// </summary>
    /// <remarks>
    /// The refill interval belongs in this threshold. A cutoff that only knows the sweep interval
    /// reclaims buckets whose refill window is still open — it was 2 x <c>CleanupInterval</c>
    /// (10 minutes by default), so any budget with a longer window lost its state, and a budget
    /// with <c>RefillRate = 0</c> lost it unconditionally.
    /// </remarks>
    private static long EvictionThreshold(TimeSpan refillInterval, TimeSpan cleanupInterval)
    {
        var ticks =
            refillInterval.Ticks > cleanupInterval.Ticks
                ? refillInterval.Ticks
                : cleanupInterval.Ticks;
        return ticks > long.MaxValue / 2 ? long.MaxValue : ticks * 2;
    }

    public ValueTask<RateLimitResult> TryConsumeTokenAsync(
        string key,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Hot path: one dictionary read, no allocation. Creating a bucket — the retired-state
        // replay, the cap check and any eviction — only happens on a key's first access.
        if (!_buckets.TryGetValue(key, out var bucket))
            bucket = AddBucket(key);

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
        _retired.Clear();
        Volatile.Write(ref _bucketCount, 0);
        Volatile.Write(ref _retiredCount, 0);
    }

    /// <summary>
    /// Cold path: the first access for <paramref name="key"/>, or the first access after the
    /// sweep or the cap evicted it. Replays the carried-forward state, inserts, and enforces
    /// <c>MaxBuckets</c>.
    /// </summary>
    private Bucket AddBucket(string key)
    {
        var candidate = new Bucket();

        // Claim the evicted state before publishing the bucket, so what other threads see is
        // already "as if the sweep had never run": TryConsume then recomputes the allowance from
        // the elapsed ticks of the original last access.
        if (_retired.TryRemove(key, out var state))
        {
            Interlocked.Decrement(ref _retiredCount);
            candidate.Tokens = state.Tokens;
            candidate.LastAccessTicks = state.LastAccessTicks;
        }

        if (_buckets.TryAdd(key, candidate))
        {
            Interlocked.Increment(ref _bucketCount);
            EnforceBucketCap();
            return candidate;
        }

        // Another thread created this key first, and its bucket is the one that counts. A claimed
        // state is dropped here: both callers are inside the same window for a key that was just
        // evicted, and the winner's fresh bucket is the pre-existing "a consumer that grabs the
        // bucket in between simply gets a fresh allowance" race — one request, once, and only for
        // a key that had already been evicted.
        return _buckets.TryGetValue(key, out var existing) ? existing : candidate;
    }

    /// <summary>
    /// Enforces <c>MaxBuckets</c>: over the cap, the least recently used buckets go. A key the
    /// store has never seen is still served — a full bucket table must not become an outage — so
    /// the cap costs state, not availability, and evicted state is carried forward rather than
    /// handed back.
    /// </summary>
    /// <remarks>
    /// Trims to 90 % of the cap and lets one thread trim: an O(n log n) snapshot per insert would
    /// make the cap a CPU amplifier under a key spray. The limit is therefore a ceiling, not an
    /// exact size, and an insert that arrives while a trim is running simply leaves the counter a
    /// little over it until the next insert checks again.
    /// </remarks>
    private void EnforceBucketCap()
    {
        if (Volatile.Read(ref _bucketCount) <= _maxBuckets)
            return;

        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
            return;

        try
        {
            var target = _maxBuckets - _maxBuckets / 10;
            var excess = Volatile.Read(ref _bucketCount) - target;
            if (excess > 0)
                EvictOldest(excess);
        }
        finally
        {
            Volatile.Write(ref _evicting, 0);
        }
    }

    /// <summary>Evicts up to <paramref name="count"/> of the least recently used buckets.</summary>
    private void EvictOldest(int count)
    {
        var snapshot = _buckets.ToArray();
        // Oldest first. LastAccessTicks is written under the bucket lock, so an atomic read is the
        // tear-free way to order buckets from outside one.
        Array.Sort(
            snapshot,
            static (a, b) =>
                LastAccessForOrdering(a.Value).CompareTo(LastAccessForOrdering(b.Value))
        );

        var nowTicks = TimeProvider.GetUtcNow().Ticks;
        for (var i = 0; i < snapshot.Length && count > 0; i++)
        {
            if (EvictBucket(snapshot[i].Key, snapshot[i].Value, nowTicks))
                count--;
        }

        // A cap-eviction pass retires state just like the sweep does, so it has to bound the
        // carried-forward map too — otherwise a key spray overflows the live map into an
        // unbounded retirement map and the cap buys nothing.
        TrimRetired(nowTicks);
    }

    /// <summary>
    /// Turns a bucket into an eviction-order key: <c>LastAccessTicks == 0</c> means "created by
    /// <see cref="AddBucket"/>, not consumed yet" (<see cref="TryConsume"/> writes the tick under
    /// the bucket lock), so it has to order as the newest bucket, not the oldest. Ordered by raw
    /// ticks, the bucket the caller is about to consume would be the first one the cap evicts —
    /// the request would still be served, but its first consume would leave no trace at all.
    /// </summary>
    private static long LastAccessForOrdering(Bucket bucket)
    {
        var ticks = Interlocked.Read(ref bucket.LastAccessTicks);
        return ticks == 0 ? long.MaxValue : ticks;
    }

    /// <summary>
    /// Removes one bucket from the live map and retires its state. The bucket lock makes the
    /// remove-and-read one step, so the state carried forward is exactly the state the bucket had
    /// when it left.
    /// </summary>
    /// <returns><see langword="true"/> when this call removed the bucket.</returns>
    private bool EvictBucket(string key, Bucket bucket, long nowTicks)
    {
        lock (bucket.Lock)
        {
            // The key/bucket pair, not the key: a bucket created for the same key after this one
            // left must not be the one that disappears.
            if (!_buckets.TryRemove(new KeyValuePair<string, Bucket>(key, bucket)))
                return false;

            Interlocked.Decrement(ref _bucketCount);
            Retire(key, bucket.Tokens, bucket.LastAccessTicks, nowTicks);
            return true;
        }
    }

    /// <summary>
    /// Carries a bucket's state forward — unless a fresh bucket would be indistinguishable from
    /// it, which is the only case in which forgetting anything is free.
    /// </summary>
    private void Retire(string key, double tokens, long lastAccessTicks, long nowTicks)
    {
        if (lastAccessTicks == 0)
            return; // never used: already exactly what a new bucket starts as

        if (ProjectTokens(tokens, lastAccessTicks, nowTicks) >= _maxTokens)
            return; // refilled to full by now: a new bucket starts here, so nothing is lost

        var carrying = new RetiredState(tokens, lastAccessTicks);
        if (_retired.TryAdd(key, carrying))
            Interlocked.Increment(ref _retiredCount);
        else
            _retired[key] = carrying; // already carried: overwrite, the count is unchanged
    }

    /// <summary>
    /// Bounds the carried-forward states by <c>MaxBuckets</c> as well, so "remember what was
    /// evicted" cannot become the unbounded growth the cap exists to stop.
    /// </summary>
    /// <remarks>
    /// Cheapest loss first. A state whose bucket has refilled to full by now <em>is</em> what a
    /// fresh bucket would be, so dropping it costs nothing; among the rest, the fullest go first,
    /// because a nearly refilled bucket has almost nothing left to remember while an empty one
    /// still owes its whole window. Least recently used breaks ties.
    /// <para>
    /// Ordering by recency alone would be the wrong trade: under the flood the cap exists to
    /// survive, every flooded key's state is nearly full, so recency would discard the handful of
    /// expensive states — the buckets someone actually spent — first, and hand them back in full.
    /// </para>
    /// <para>
    /// What is dropped here is genuinely forgotten: past <c>MaxBuckets</c> live buckets and
    /// <c>MaxBuckets</c> evicted states, a bounded store cannot remember anything, and that
    /// residual risk is the price of not growing without bound. Reaching it costs a caller
    /// <c>2 x MaxBuckets</c> distinct keys, which is the same order as the key-rotation bypass
    /// <see cref="RateLimitKeys.Header"/> documents — the cap cannot fix key trust.
    /// </para>
    /// </remarks>
    private void TrimRetired(long nowTicks)
    {
        if (Volatile.Read(ref _retiredCount) <= _maxBuckets)
            return;

        var snapshot = _retired.ToArray();
        var drop = snapshot.Length - _maxBuckets;

        Array.Sort(
            snapshot,
            (a, b) =>
            {
                var left = ProjectTokens(a.Value.Tokens, a.Value.LastAccessTicks, nowTicks);
                var right = ProjectTokens(b.Value.Tokens, b.Value.LastAccessTicks, nowTicks);
                var fullestFirst = right.CompareTo(left);
                return fullestFirst != 0
                    ? fullestFirst
                    : a.Value.LastAccessTicks.CompareTo(b.Value.LastAccessTicks);
            }
        );

        for (var i = 0; i < snapshot.Length && drop > 0; i++)
        {
            if (_retired.TryRemove(snapshot[i]))
            {
                Interlocked.Decrement(ref _retiredCount);
                drop--;
            }
        }
    }

    /// <summary>
    /// What a bucket holding <paramref name="tokens"/> since <paramref name="lastAccessTicks"/>
    /// holds at <paramref name="nowTicks"/>: the arithmetic <see cref="TryConsume"/> applies, so
    /// "carry the state forward" and "recompute from elapsed" are one expression.
    /// </summary>
    private double ProjectTokens(double tokens, long lastAccessTicks, long nowTicks)
    {
        if (lastAccessTicks == 0)
            return _maxTokens; // a never-used bucket starts full (see TryConsume)

        if (_refillRate <= 0 || nowTicks <= lastAccessTicks)
            return tokens;

        var elapsed = nowTicks - lastAccessTicks;
        return Math.Min(tokens + elapsed / _refillIntervalTicks * _refillRate, _maxTokens);
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

    /// <summary>
    /// Reclaims buckets that have been idle long enough to be worthless. Internal test seam: the
    /// timer calls it, and the tests call it directly so the sweep is deterministic instead of
    /// racing a 50 ms timer.
    /// </summary>
    internal void CleanupExpired()
    {
        var nowTicks = TimeProvider.GetUtcNow().Ticks;
        // Saturating: a threshold that does not fit below `now` must mean "nothing is eligible",
        // not wrap around into a cutoff that reclaims every bucket.
        var cutoff =
            _evictionThresholdTicks >= nowTicks
                ? long.MinValue
                : nowTicks - _evictionThresholdTicks;

        foreach (var (id, bucket) in _buckets)
        {
            // Read under the bucket lock: LastAccessTicks is a plain long written by the consume
            // path (TryConsume), so an unsynchronised read can tear on 32-bit targets and evict a
            // live bucket. A consumer that grabs the bucket in between still keeps its allowance:
            // the bucket it holds carries its own state either way.
            bool expired;
            lock (bucket.Lock)
            {
                expired = bucket.LastAccessTicks < cutoff;
            }

            if (expired)
                EvictBucket(id, bucket, nowTicks);
        }

        TrimRetired(nowTicks);
    }

    private sealed class Bucket
    {
        public double Tokens;
        public long LastAccessTicks;
        public readonly object Lock = new();
    }

    /// <summary>
    /// The part of a bucket a fresh bucket cannot reproduce: its remaining allowance, and the tick
    /// that allowance is measured from. A struct, so remembering an evicted bucket costs no
    /// object of its own.
    /// </summary>
    private readonly record struct RetiredState(double Tokens, long LastAccessTicks);
}
