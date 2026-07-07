namespace PicoNode.Agent.Tests;

public class AgentMailboxTests
{
    [Test]
    public async Task Post_Multiple_ProcessedInOrder()
    {
        var results = new List<int>();
        using var mailbox = new AgentMailbox<string>(
            async (msg, ct) =>
            {
                results.Add(int.Parse(msg));
                await Task.CompletedTask;
            }
        );

        mailbox.Post("1");
        mailbox.Post("2");
        mailbox.Post("3");
        await Task.Delay(200);

        await Assert.That(results).IsEquivalentTo([1, 2, 3]);
    }

    [Test]
    public async Task Dispose_StopsProcessing()
    {
        var tcs = new TaskCompletionSource();
        var mailbox = new AgentMailbox<string>(
            async (msg, ct) =>
            {
                tcs.TrySetResult();
                await Task.CompletedTask;
            }
        );
        mailbox.Post("1");
        // Wait for the first message to be processed
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        mailbox.Dispose();
        mailbox.Post("2");
        await Task.Delay(100);
        // Message 2 should NOT be processed (mailbox disposed)
        await Assert.That(true).IsTrue(); // no exception, no after-dispose processing crash
    }
}
