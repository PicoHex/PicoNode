namespace PicoNode.Web.Tests;

public sealed class InMemoryRateLimitStoreTests
{
    private static RateLimitOptions DefaultOptions =>
        new()
        {
            MaxTokens = 3,
            RefillRate = 1,
            RefillInterval = TimeSpan.FromSeconds(1),
            KeySelector = static _ => "fixed-key",
        };

    [Test]
    public async Task Single_request_is_allowed()
    {
        using var store = new InMemoryRateLimitStore(DefaultOptions);

        var result = await store.TryConsumeTokenAsync("user-1");

        await Assert.That(result.Allowed).IsTrue();
        await Assert.That(result.Remaining).IsEqualTo(2);
        await Assert.That(result.Limit).IsEqualTo(3);
    }

    [Test]
    public async Task MaxTokens_requests_all_allowed()
    {
        using var store = new InMemoryRateLimitStore(DefaultOptions);

        for (int i = 0; i < 3; i++)
        {
            var result = await store.TryConsumeTokenAsync("user-1");
            await Assert.That(result.Allowed).IsTrue();
        }
    }

    [Test]
    public async Task Exceed_max_tokens_gets_rate_limited()
    {
        using var store = new InMemoryRateLimitStore(DefaultOptions);

        for (int i = 0; i < 3; i++)
            await store.TryConsumeTokenAsync("user-1");

        var result = await store.TryConsumeTokenAsync("user-1");

        await Assert.That(result.Allowed).IsFalse();
        await Assert.That(result.Remaining).IsEqualTo(0);
        await Assert.That(result.NextAvailableAt).IsGreaterThan(0);
    }

    [Test]
    public async Task Different_keys_have_independent_buckets()
    {
        using var store = new InMemoryRateLimitStore(DefaultOptions);

        for (int i = 0; i < 3; i++)
            await store.TryConsumeTokenAsync("user-1");

        var resultA = await store.TryConsumeTokenAsync("user-1");
        var resultB = await store.TryConsumeTokenAsync("user-2");

        await Assert.That(resultA.Allowed).IsFalse();
        await Assert.That(resultB.Allowed).IsTrue();
    }

    [Test]
    public async Task Bucket_refills_after_interval()
    {
        using var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                MaxTokens = 1,
                RefillRate = 1,
                RefillInterval = TimeSpan.FromMilliseconds(100),
                KeySelector = static _ => "k",
            }
        );

        await store.TryConsumeTokenAsync("k");
        var resultBefore = await store.TryConsumeTokenAsync("k");
        await Assert.That(resultBefore.Allowed).IsFalse();

        await Task.Delay(200);
        var resultAfter = await store.TryConsumeTokenAsync("k");
        await Assert.That(resultAfter.Allowed).IsTrue();
    }

    [Test]
    public async Task Constructor_throws_on_invalid_max_tokens()
    {
        await Assert
            .That(() =>
                new InMemoryRateLimitStore(
                    new RateLimitOptions { MaxTokens = 0, KeySelector = static _ => "k" }
                )
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Zero_refill_rate_never_refills()
    {
        // RefillRate=0 is a legitimate fixed-window configuration — the bucket
        // must never refill, and the retry/reset timestamps must stay finite
        // (the old code divided by zero and produced garbage long values).
        var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                MaxTokens = 1,
                RefillRate = 0,
                RefillInterval = TimeSpan.FromHours(1),
                KeySelector = static _ => "k",
            }
        );

        var first = await store.TryConsumeTokenAsync("k");
        var second = await store.TryConsumeTokenAsync("k");

        await Assert.That(first.Allowed).IsTrue();
        await Assert
            .That(second.Allowed)
            .IsFalse()
            .Because("a zero refill rate must never replenish the bucket");
        await Assert
            .That(second.NextAvailableAt)
            .IsGreaterThanOrEqualTo(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Because("the retry timestamp must be finite and in the future");
        await Assert
            .That(second.ResetAt)
            .IsGreaterThanOrEqualTo(second.NextAvailableAt)
            .Because("the reset timestamp must be finite");
    }

    [Test]
    public async Task Constructor_throws_on_zero_cleanup_interval()
    {
        await Assert
            .That(() =>
                new InMemoryRateLimitStore(
                    new RateLimitOptions
                    {
                        MaxTokens = 5,
                        RefillRate = 1,
                        CleanupInterval = TimeSpan.Zero,
                        KeySelector = static _ => "k",
                    }
                )
            )
            .Throws<ArgumentOutOfRangeException>()
            .Because(
                "CleanupInterval=0 makes the cleanup timer fire once and never again, "
                    + "leaking buckets under attack"
            );
    }

    [Test]
    public async Task Eviction_does_not_restore_a_full_bucket()
    {
        // RefillRate=0 is the sharpest probe: a fixed-window bucket can only allow another
        // request if it was replaced by a full one, so "still rejected after the sweep" is
        // exactly the no-bypass property. MaxTokens=2 means the bucket is spent after two.
        var clock = new StoreClock();
        using var store = new InMemoryRateLimitStore(
            RateLimitBudget.PerMinute(2, 0),
            TimeSpan.FromMilliseconds(50)
        )
        {
            TimeProvider = clock,
        };

        await Assert.That((await store.TryConsumeTokenAsync("k")).Allowed).IsTrue();
        await Assert.That((await store.TryConsumeTokenAsync("k")).Allowed).IsTrue();
        await Assert.That((await store.TryConsumeTokenAsync("k")).Allowed).IsFalse();

        // Idle for far longer than 2 x max(refillInterval, cleanupInterval) = 2 minutes: the
        // bucket is eligible for eviction, so sweep it.
        clock.Advance(TimeSpan.FromMinutes(10));
        store.CleanupExpired();

        await Assert
            .That(store.BucketCount)
            .IsEqualTo(0)
            .Because(
                "an idle bucket must leave the live map, or key spraying grows it without bound"
            );

        var afterEviction = await store.TryConsumeTokenAsync("k");

        await Assert
            .That(afterEviction.Allowed)
            .IsFalse()
            .Because(
                "eviction must not hand the allowance back: a recreated bucket that starts full "
                    + "silently shortens every window longer than the sweep interval"
            );
        await Assert.That(afterEviction.Remaining).IsEqualTo(0);
    }

    [Test]
    public async Task Eviction_carries_a_partially_refilled_bucket_forward()
    {
        // 10 tokens refilling at 1 per minute: 3 minutes after the bucket was emptied it is not
        // full again (3 of the 10 are back) — exactly the case the old sweep got wrong. A
        // recreated bucket answers with a full 10 (Remaining 9); the carried state answers with
        // 3 tokens (Remaining 2).
        var clock = new StoreClock();
        using var store = new InMemoryRateLimitStore(
            new RateLimitBudget(10, 1, TimeSpan.FromMinutes(1)),
            TimeSpan.FromMilliseconds(50)
        )
        {
            TimeProvider = clock,
        };

        for (var i = 0; i < 10; i++)
            await store.TryConsumeTokenAsync("k");

        await Assert.That((await store.TryConsumeTokenAsync("k")).Allowed).IsFalse();

        clock.Advance(TimeSpan.FromMinutes(3));
        store.CleanupExpired();
        await Assert.That(store.BucketCount).IsEqualTo(0);

        var carried = await store.TryConsumeTokenAsync("k");

        await Assert.That(carried.Allowed).IsTrue();
        await Assert
            .That(carried.Remaining)
            .IsEqualTo(2)
            .Because(
                "the recreated bucket must continue from the evicted state: 3 minutes of refill "
                    + "at 1/minute is 3 tokens, so one consume leaves 2 — a fresh bucket (9 left) "
                    + "would be the bypass"
            );
    }

    [Test]
    public async Task Buckets_are_bounded_by_MaxBuckets()
    {
        // Header- and path-derived keys are attacker-controlled: 100 000 of them must not become
        // 100 000 dictionary entries that never leave. MaxBuckets=10 keeps the arithmetic small.
        using var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                MaxTokens = 100,
                RefillRate = 1,
                RefillInterval = TimeSpan.FromHours(1),
                MaxBuckets = 10,
                KeySelector = static _ => "fixed-key",
            }
        );

        for (var i = 0; i < 100; i++)
            await Assert.That((await store.TryConsumeTokenAsync($"spray-{i}")).Allowed).IsTrue();

        await Assert
            .That(store.BucketCount)
            .IsLessThanOrEqualTo(10)
            .Because("MaxBuckets is the only bound on an attacker-rotated key space");

        await Assert
            .That(store.RetiredCount)
            .IsLessThanOrEqualTo(10)
            .Because(
                "the carried-forward states need the same bound: unbounded retirement would "
                    + "just move the growth one step aside"
            );

        // The cap must not become an outage: a key it has never seen is still served.
        await Assert.That((await store.TryConsumeTokenAsync("after-the-spray")).Allowed).IsTrue();
    }

    [Test]
    public async Task Sweep_respects_the_refill_window_not_just_the_sweep_interval()
    {
        // A five-token bucket refilling once an hour, swept every 50 ms. The old cutoff was
        // 2 x CleanupInterval = 100 ms and called this bucket idle after 100 ms — with essentially
        // its whole window still open. The threshold is 2 x max(refillInterval, cleanupInterval),
        // so a bucket that is still earning tokens stays live.
        var clock = new StoreClock();
        using var store = new InMemoryRateLimitStore(
            new RateLimitBudget(5, 1, TimeSpan.FromHours(1)),
            TimeSpan.FromMilliseconds(50)
        )
        {
            TimeProvider = clock,
        };

        await store.TryConsumeTokenAsync("k");

        clock.Advance(TimeSpan.FromSeconds(10));
        store.CleanupExpired();

        await Assert
            .That(store.BucketCount)
            .IsEqualTo(1)
            .Because("10 seconds is nowhere near idle for a bucket with a one-hour refill window");
    }

    [Test]
    public async Task Cap_eviction_does_not_flush_a_live_bucket()
    {
        // The flush attack the cap could introduce: spend a bucket, then flood the store with
        // fresh keys until your own bucket is pushed out. RefillRate=0, so a bucket recreated full
        // is the only way the last request below can be allowed again.
        using var store = new InMemoryRateLimitStore(
            new RateLimitOptions
            {
                MaxTokens = 2,
                RefillRate = 0,
                RefillInterval = TimeSpan.FromMinutes(1),
                MaxBuckets = 10,
                KeySelector = static _ => "fixed-key",
            }
        );

        await Assert.That((await store.TryConsumeTokenAsync("hot")).Allowed).IsTrue();
        await Assert.That((await store.TryConsumeTokenAsync("hot")).Allowed).IsTrue();

        for (var i = 0; i < 100; i++)
            await store.TryConsumeTokenAsync($"flood-{i}");

        await Assert.That(store.BucketCount).IsLessThanOrEqualTo(10);

        await Assert
            .That((await store.TryConsumeTokenAsync("hot")).Allowed)
            .IsFalse()
            .Because(
                "a flood must not flush a live bucket's spend: the flooded keys' states are "
                    + "nearly full and get retired first, so the cap cannot become the bypass"
            );
    }

    [Test]
    public async Task Constructor_throws_on_non_positive_max_buckets()
    {
        await Assert
            .That(() =>
                new InMemoryRateLimitStore(
                    new RateLimitOptions { MaxBuckets = 0, KeySelector = static _ => "k" }
                )
            )
            .Throws<ArgumentOutOfRangeException>()
            .Because("a store allowed to keep no bucket would evict every key on arrival");
    }

    [Test]
    public async Task Idle_bucket_is_reclaimed_by_the_cleanup_timer()
    {
        // The cleanup timer is a real timer whose period is min(RefillInterval, CleanupInterval);
        // the clock it measures idleness with is the manual one, so advancing the clock is what
        // makes a bucket look abandoned. RefillRate=0 is what makes the observation meaningful:
        // a bucket that never refills can only allow a request again if eviction reset it.
        var clock = new StoreClock();
        using var store = new InMemoryRateLimitStore(
            RateLimitBudget.PerSecond(2, 0),
            TimeSpan.FromMilliseconds(50)
        )
        {
            TimeProvider = clock,
        };

        await Assert.That((await store.TryConsumeTokenAsync("k")).Remaining).IsEqualTo(1);
        await Assert.That((await store.TryConsumeTokenAsync("k")).Remaining).IsEqualTo(0);
        await Assert.That((await store.TryConsumeTokenAsync("k")).Allowed).IsFalse();

        var reclaimed = false;
        // 200 x 50 ms is insurance only: the usual answer arrives on the first attempt, and a
        // starved CI runner (real timer, no seam to advance it) must not fail the test.
        for (var attempt = 0; attempt < 200 && !reclaimed; attempt++)
        {
            // Idle for far longer than 2 x max(refillInterval, cleanupInterval) = 2 seconds, then
            // give the cleanup timer (50 ms cadence) a window to fire before looking again.
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(50);
            reclaimed = store.BucketCount == 0;
        }

        await Assert
            .That(reclaimed)
            .IsTrue()
            .Because("the timer must reclaim idle buckets, or key spraying grows the map forever");

        await Assert
            .That((await store.TryConsumeTokenAsync("k")).Allowed)
            .IsFalse()
            .Because("the reclaimed bucket still owes its spend; eviction is not a reset");
    }

    /// <summary>
    /// Manual clock for the eviction test. Must override <c>GetUtcNow</c> (the store reads it; the
    /// keep-alive <see cref="ManualTimeProvider"/> only drives timers) and start at a non-zero
    /// epoch, because <c>TryConsume</c> treats <c>LastAccessTicks == 0</c> as a first access.
    /// </summary>
    private sealed class StoreClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Test]
    public async Task Dispose_throws_on_subsequent_calls()
    {
        var store = new InMemoryRateLimitStore(DefaultOptions);
        store.Dispose();

        await Assert
            .That(async () => await store.TryConsumeTokenAsync("k"))
            .Throws<ObjectDisposedException>();
    }
}
