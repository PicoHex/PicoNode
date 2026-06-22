namespace PicoNode.Web.Tests;

public sealed class InMemoryRateLimitStoreTests
{
    private static RateLimitOptions DefaultOptions => new()
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
        using var store = new InMemoryRateLimitStore(new RateLimitOptions
        {
            MaxTokens = 1,
            RefillRate = 1,
            RefillInterval = TimeSpan.FromMilliseconds(100),
            KeySelector = static _ => "k",
        });

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
        await Assert.That(() =>
                new InMemoryRateLimitStore(
                    new RateLimitOptions
                    {
                        MaxTokens = 0,
                        KeySelector = static _ => "k",
                    }))
            .Throws<ArgumentOutOfRangeException>();
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
