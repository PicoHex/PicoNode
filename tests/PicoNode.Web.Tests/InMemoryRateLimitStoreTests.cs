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
    public async Task Idle_bucket_is_evicted_and_starts_full_again()
    {
        // The cleanup timer is a real timer whose period is min(RefillInterval, CleanupInterval);
        // the clock it measures idleness with is the manual one, so advancing the clock is what
        // makes a bucket look abandoned. RefillRate=0 is what makes this observation meaningful:
        // a bucket that never refills can only allow a request again if it was reclaimed and
        // recreated (a fresh bucket starts full: MaxTokens=2, so one consume leaves 1).
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

        var evicted = false;
        // 200 x 50 ms is insurance only: the usual answer arrives on the first attempt, and a
        // starved CI runner (real timer, no seam to advance it) must not fail the test.
        for (var attempt = 0; attempt < 200 && !evicted; attempt++)
        {
            // Idle for far longer than 2 x CleanupInterval, then give the cleanup timer (50 ms
            // cadence) a window to fire before the probe touches the bucket again.
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(50);
            evicted = (await store.TryConsumeTokenAsync("k")).Remaining == 1;
        }

        await Assert
            .That(evicted)
            .IsTrue()
            .Because(
                "an idle bucket must be reclaimed (and a recreated one starts full), "
                    + "otherwise key-spraying grows the dictionary without bound"
            );
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
