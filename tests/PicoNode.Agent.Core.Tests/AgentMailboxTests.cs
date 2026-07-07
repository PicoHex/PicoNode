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
        var results = new List<int>();
        var mailbox = new AgentMailbox<string>(
            async (msg, ct) =>
            {
                results.Add(int.Parse(msg));
                await Task.Delay(1000, ct);
            }
        );
        mailbox.Post("1");
        mailbox.Dispose();
        await Task.Delay(100);
        mailbox.Post("2");
        await Task.Delay(100);
        await Assert.That(results.Count).IsEqualTo(1);
    }
}
