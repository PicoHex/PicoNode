namespace PicoAgent.Tests;

public sealed class SessionRestoreTests
{
    [Test]
    public async Task CreateAsync_RestoresAllSessionsFromDisk()
    {
        // Arrange: create temp home dir with two session JSONL files
        var tmp = Path.Combine(Path.GetTempPath(), $"picoagent-test-{Guid.NewGuid():N}");
        var sessionsDir = Path.Combine(tmp, "sessions");
        Directory.CreateDirectory(sessionsDir);

        try
        {
            // Session "alpha" — one user message
            await File.WriteAllTextAsync(
                Path.Combine(sessionsDir, "alpha.jsonl"),
                """{"type":"session","version":3,"timestamp":"2026-07-03T00:00:00Z"}"""
                    + "\n"
                    + """{"$type":"message","Timestamp":"2026-07-03T00:00:01Z","ParentId":null,"Id":"a1","Message":{"Role":"user","Timestamp":1,"Content":"hello alpha","ContentBlocks":null,"Model":"","Provider":"","Api":0,"Usage":{"InputTokens":0,"OutputTokens":0},"StopReason":"stop","ErrorMessage":null,"ToolCallId":null,"ToolName":null,"IsError":false}}"""
                    + "\n"
            );

            // Session "beta" — one user message
            await File.WriteAllTextAsync(
                Path.Combine(sessionsDir, "beta.jsonl"),
                """{"type":"session","version":3,"timestamp":"2026-07-03T00:00:00Z"}"""
                    + "\n"
                    + """{"$type":"message","Timestamp":"2026-07-03T00:00:02Z","ParentId":null,"Id":"b1","Message":{"Role":"user","Timestamp":2,"Content":"hello beta","ContentBlocks":null,"Model":"","Provider":"","Api":0,"Usage":{"InputTokens":0,"OutputTokens":0},"StopReason":"stop","ErrorMessage":null,"ToolCallId":null,"ToolName":null,"IsError":false}}"""
                    + "\n"
            );

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

            // Act
            await using var agent = await Agent.CreateAsync(config, tmp);

            // Assert: both sessions should have messages restored
            var alphaMsgs = await agent.GetMessagesAsync("alpha");
            var betaMsgs = await agent.GetMessagesAsync("beta");

            await Assert.That(alphaMsgs.Count).IsGreaterThan(0);
            await Assert.That(betaMsgs.Count).IsGreaterThan(0);

            await Assert.That(alphaMsgs[0].Content).IsEqualTo("hello alpha");
            await Assert.That(betaMsgs[0].Content).IsEqualTo("hello beta");
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch
            { /* cleanup best-effort */
            }
        }
    }
}
