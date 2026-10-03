namespace PicoNode.Web.Tests;

/// <summary>
/// Makes the store's per-bucket lock observable. Dropping <c>lock (bucket.Lock)</c> leaves every
/// other store test green, and a pure race wins only on roomy machines: threads must hit a
/// few-nanosecond read-modify-write window before the scheduler spreads them out, so a
/// core-count-derived racer count silently stops working on a 2-4 vCPU CI runner.
/// <para>
/// This test does not rely on that window. The store reads <c>TimeProvider.GetUtcNow()</c>
/// <em>inside</em> the critical section, so a clock implementation that yields while it runs is a
/// direct probe for the lock: with mutual exclusion no two callers can be inside at once, and
/// without it the racers cannot help but overlap — the first one to enter hands the CPU to the
/// next, which works on any core count, down to a single core. <see cref="LockProbeClock"/>
/// reports such an overlap, and the round structure adds a behavioural invariant on top.</para>
/// <para>
/// The two assertions catch different mistakes, which is why both are here. The overlap count is
/// what detects a missing lock: a lost update cannot be relied on to show up as *extra* grants,
/// because each racer consumes at most once per round (Racers == Tokens is the ceiling, not just
/// the expectation). The exact total therefore detects the opposite failure — a racer that was
/// refused a token every other racer got — e.g. a stubbed-out or over-eager short-circuit.
/// </para>
/// </summary>
public sealed class InMemoryRateLimitStoreConcurrencyTests
{
    private const int Tokens = 4;
    private const int Racers = Tokens; // every racer must be on the grant path each round
    private const int Rounds = 25;

    /// <summary>
    /// Large enough that the store's cleanup timer (real time, `min(refill, cleanup)`) never fires
    /// during the test: its clock is the manual one below, and a cleanup running beside the racers
    /// would enter the probe clock outside the bucket lock and report a false overlap.
    /// </summary>
    private static readonly TimeSpan Refill = TimeSpan.FromHours(1);

    [Test]
    public async Task Racers_On_One_Bucket_Are_Served_One_At_A_Time()
    {
        var clock = new LockProbeClock();
        using var store = new InMemoryRateLimitStore(
            new RateLimitBudget(Tokens, 1, Refill),
            TimeSpan.FromHours(1)
        )
        {
            TimeProvider = clock,
        };

        // +1: this thread conducts the rounds, so it releases the racers and waits for them.
        using var gate = new Barrier(Racers + 1);
        var grants = new int[Racers];
        var failures = new Exception?[Racers];
        var workers = new Task[Racers];
        for (var i = 0; i < Racers; i++)
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

        for (var i = 0; i < Racers; i++)
            await Assert.That(failures[i]).IsNull().Because($"racer {i} threw");

        await Assert
            .That(clock.Overlaps)
            .IsZero()
            .Because(
                "the critical section reads the clock while holding the bucket lock, so two "
                    + "racers inside it at once means the lock is not doing its job"
            );

        var total = 0;
        foreach (var grant in grants)
            total += grant;

        await Assert
            .That(total)
            .IsEqualTo(Rounds * Tokens)
            .Because(
                $"every round refills exactly {Tokens} tokens into an empty bucket and every one "
                    + $"of the {Racers} racers must take one: a lower total means a racer was "
                    + "refused a token another racer was given"
            );
    }

    /// <summary>
    /// The store's clock, doubling as a lock probe: it yields (2 ms) while it runs, so a second
    /// caller arriving without mutual exclusion is not a matter of luck. Must override
    /// <c>GetUtcNow</c> (the store reads it; the keep-alive <see cref="ManualTimeProvider"/> only
    /// drives timers) and start at a non-zero epoch, because <c>TryConsume</c> treats
    /// <c>LastAccessTicks == 0</c> as a first access that starts the bucket full.
    /// </summary>
    private sealed class LockProbeClock : TimeProvider
    {
        private const int YieldMs = 2;

        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private int _inside;

        /// <summary>How often two callers were inside <see cref="GetUtcNow"/> at the same time.</summary>
        public int Overlaps;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _inside) > 1)
                Interlocked.Increment(ref Overlaps);

            try
            {
                Thread.Sleep(YieldMs);
                return _now;
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }

        /// <summary>Called by the conductor between rounds, never while the racers run.</summary>
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
