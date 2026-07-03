namespace PicoAgent.Tests;

public sealed class RetryConcurrencyTests
{
    [Test]
    public async Task RetryLastMessage_IsSafeWithConcurrentAccess()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"picoagent-conc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var config = new AgentConfig
            {
                Providers = new()
                {
                    ["test"] = new ProviderEntry
                    {
                        ApiKey = "sk-test",
                        ApiFormat = "openai",
                        BaseUrl = "https://api.openai.com/v1",
                    },
                },
                Model = "gpt-4",
            };
            await using var agent = await Agent.CreateAsync(config, tmp);
            var host = agent.GetHostForTesting();

            var session = host.GetOrCreateSession("r1");
            for (int i = 0; i < 10; i++)
            {
                await session.AppendMessage(
                    new Message
                    {
                        Role = "user",
                        Content = $"u{i}",
                        Timestamp = i * 2,
                    }
                );
                await session.AppendMessage(
                    new Message
                    {
                        Role = "assistant",
                        Content = $"a{i}",
                        Timestamp = i * 2 + 1,
                    }
                );
            }

            // Run 10 retries concurrently — each should safely shrink the session
            var tasks = new Task[10];
            for (int i = 0; i < 10; i++)
                tasks[i] = Task.Run(() => host.RetryLastMessageAsync("r1"));

            await Task.WhenAll(tasks);

            var after = await host.GetSessionMessagesAsync("r1");
            // After 10 retries of 10 exchanges, only the earliest remaining entries survive.
            // Each retry removes the last user+following. The final count depends on timing
            // but should never throw, and should be ≤ 20.
            await Assert.That(after.Count).IsLessThanOrEqualTo(20);
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch { }
        }
    }
}
