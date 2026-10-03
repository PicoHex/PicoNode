namespace PicoNode.Web.Tests;

/// <summary>
/// Makes the store's per-bucket lock observable, which no sequential test can do: dropping
/// <c>lock (bucket.Lock)</c> leaves every other store test green.
/// <para>
/// The clock is manual and advances only <em>between</em> rounds, never while the racers run (a
/// <see cref="Barrier"/> fences both transitions), so the expected grant count is exact and is a
/// fact about the clock rather than about machine load: each round refills exactly <see
/// cref="Tokens"/> tokens into an empty bucket, so <see cref="Racers"/> threads released at the
/// same instant must still produce exactly <see cref="Tokens"/> grants. Unlocked, the refill
/// read-modify-write lets two racers both see the same pre-round bucket (and both grant), and two
/// granters can both read <c>Tokens</c> before either subtracts — either way the round overshoots.
/// Oversubscribing the cores and racing more than one token per round keep that window wide.
/// </para>
/// </summary>
public sealed class InMemoryRateLimitStoreConcurrencyTests
{
    private const int Tokens = 8;
    private const int Rounds = 500;
    private static readonly TimeSpan Refill = TimeSpan.FromSeconds(1);

    [Test]
    public async Task Racers_On_One_Bucket_Take_Exactly_The_Refilled_Tokens()
    {
        // Two threads per core: a thread preempted inside the critical section is what a missing
        // lock turns into an overshoot, so oversubscription is the point.
        var racers = Math.Clamp(2 * Environment.ProcessorCount, 8, 32);
        var clock = new StoreClock();
        using var store = new InMemoryRateLimitStore(
            RateLimitBudget.PerSecond(Tokens, 1),
            TimeSpan.FromHours(1)
        )
        {
            TimeProvider = clock,
        };

        // +1: this thread conducts the rounds, so it releases the racers and waits for them.
        using var gate = new Barrier(racers + 1);
        var grants = new int[racers];
        var failures = new Exception?[racers];
        var workers = new Task[racers];
        for (var i = 0; i < racers; i++)
        {
            var slot = i;
            workers[i] = Task.Factory.StartNew(
                () =>
                {
                    for (var round = 0; round < Rounds; round++)
                    {
                        gate.SignalAndWait();
                        try
                        {
                            if (store.TryConsumeTokenAsync("hot").GetAwaiter().GetResult().Allowed)
                                grants[slot]++;
                        }
                        catch (Exception ex)
                        {
                            // Never let a worker skip the closing signal: a broken barrier
                            // would hang the test instead of failing it.
                            failures[slot] = ex;
                        }

                        gate.SignalAndWait();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        var conductor = Task.Run(() =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                clock.Advance(Refill * Tokens); // exactly Tokens tokens become available
                gate.SignalAndWait(); // release the racers
                gate.SignalAndWait(); // wait until every racer has consumed
            }
        });

        await Task.WhenAll(workers.Append(conductor)).WaitAsync(TimeSpan.FromSeconds(120));

        for (var i = 0; i < racers; i++)
            await Assert.That(failures[i]).IsNull().Because($"racer {i} threw");

        var total = 0;
        foreach (var grant in grants)
            total += grant;

        await Assert
            .That(total)
            .IsEqualTo(Rounds * Tokens)
            .Because(
                $"every round refills exactly {Tokens} tokens into an empty bucket, so "
                    + $"{racers} simultaneous racers must produce exactly {Tokens} grants per "
                    + "round; more means two racers read the bucket at once"
            );
    }

    /// <summary>
    /// Must override <c>GetUtcNow</c> (the store reads it; the keep-alive
    /// <see cref="ManualTimeProvider"/> only drives timers) and start at a non-zero epoch:
    /// <c>TryConsume</c> treats <c>LastAccessTicks == 0</c> as a first access that starts the
    /// bucket full, which would hand out a free round.
    /// </summary>
    private sealed class StoreClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
