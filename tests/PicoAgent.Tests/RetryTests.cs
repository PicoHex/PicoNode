namespace PicoAgent.Tests;

public sealed class RetryTests
{
    [Test]
    public async Task RetryLastMessage_RemovesLastExchange()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"picoagent-retry-{Guid.NewGuid():N}");
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

            // Append 2 user messages manually
            var session = host.GetOrCreateSession("r1");
            await session.AppendMessage(
                new Message
                {
                    Role = "user",
                    Content = "msg1",
                    Timestamp = 1,
                }
            );
            await session.AppendMessage(
                new Message
                {
                    Role = "assistant",
                    Content = "reply1",
                    Timestamp = 2,
                }
            );
            await session.AppendMessage(
                new Message
                {
                    Role = "user",
                    Content = "msg2",
                    Timestamp = 3,
                }
            );
            await session.AppendMessage(
                new Message
                {
                    Role = "assistant",
                    Content = "reply2",
                    Timestamp = 4,
                }
            );

            var before = await host.GetSessionMessagesAsync("r1");
            var userBefore = before.Count(m => m.Role == "user");

            // Retry: should remove msg2 + reply2
            await host.RetryLastMessageAsync("r1");

            var after = await host.GetSessionMessagesAsync("r1");
            var userAfter = after.Count(m => m.Role == "user");

            // User count should decrease by 1 (msg2 removed)
            await Assert.That(userAfter).IsEqualTo(userBefore - 1);
            // Only msg1 should remain
            await Assert.That(after[0].Content).IsEqualTo("msg1");
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
