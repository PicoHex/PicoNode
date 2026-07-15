using System.Collections.Concurrent;

namespace PicoNode.Agent.Core.Tests;

/// <summary>
/// TDD: PerSessionLock allows different sessions to proceed concurrently
/// while serializing the same session.
/// </summary>
public sealed class PerSessionLockTests
{
    [Test]
    public async Task DifferentSessions_CanProceedConcurrently()
    {
        var lck = new PicoAgent.PerSessionLock();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        var entered = new ConcurrentQueue<string>();
        var hold1 = new TaskCompletionSource();

        var task1 = Task.Run(async () =>
        {
            using (await lck.AcquireAsync(s1, CancellationToken.None))
            {
                entered.Enqueue("s1");
                await hold1.Task;
            }
        });

        await Task.Delay(200);

        using (await lck.AcquireAsync(s2, CancellationToken.None))
        {
            entered.Enqueue("s2");
        }

        hold1.TrySetResult();
        await task1;

        await Assert.That(entered).Contains("s2");
        await Assert.That(entered).Contains("s1");
    }

    [Test]
    public async Task SameSession_IsSerialized()
    {
        var lck = new PicoAgent.PerSessionLock();
        var sid = Guid.NewGuid();

        var order = new ConcurrentQueue<int>();
        var hold1 = new TaskCompletionSource();

        var task1 = Task.Run(async () =>
        {
            using (await lck.AcquireAsync(sid, CancellationToken.None))
            {
                order.Enqueue(1);
                await hold1.Task;
            }
        });

        await Task.Delay(200);

        var task2 = Task.Run(async () =>
        {
            using (await lck.AcquireAsync(sid, CancellationToken.None))
            {
                order.Enqueue(2);
            }
        });

        await Task.Delay(200);

        // task2 should NOT have entered yet (same session, blocked)
        await Assert.That(order).DoesNotContain(2);

        hold1.TrySetResult();
        await Task.WhenAll(task1, task2);

        await Assert.That(order.ToArray()).IsEquivalentTo([1, 2]);
    }
}
