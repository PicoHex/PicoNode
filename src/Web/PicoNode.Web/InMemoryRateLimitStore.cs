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

    /// <summary>Retry-After/Reset timestamp when RefillRate=0 (fixed window, 1 year).</summary>
    private const long NoRefillRetryAfterSeconds = 31_536_000;

    public InMemoryRateLimitStore(RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxTokens, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.RefillInterval, TimeSpan.Zero);
        // RefillRate=0 is legal (fixed window, never refills) — the refill math
        // below guards against it explicitly.
        // CleanupInterval=0 makes Timer fire exactly once — buckets would
        // never be reclaimed (unbounded growth under key-spray attacks).
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CleanupInterval, TimeSpan.Zero);

        _maxTokens = options.MaxTokens;
        _refillRate = options.RefillRate;
        _refillIntervalTicks = options.RefillInterval.Ticks;
        _cleanupIntervalTicks = options.CleanupInterval.Ticks;

        var cleanupInterval = options.CleanupInterval;
        var interval =
            options.RefillInterval < cleanupInterval ? options.RefillInterval : cleanupInterval;

        _cleanupTimer = new Timer(_ => CleanupExpired(), null, interval, interval);
    }

    public ValueTask<RateLimitResult> TryConsumeTokenAsync(
        string key,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        var now = DateTimeOffset.UtcNow.Ticks;

        lock (bucket.Lock)
        {
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
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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
        var cutoff = DateTimeOffset.UtcNow.Ticks - _cleanupIntervalTicks * 2;

        foreach (var (id, bucket) in _buckets)
        {
            if (bucket.LastAccessTicks < cutoff)
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
