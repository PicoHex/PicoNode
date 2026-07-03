namespace PicoAgent.Tests;

public sealed class SessionDeleteTests
{
    [Test]
    public async Task DeleteSession_RemovesFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"picoagent-del-{Guid.NewGuid():N}");
        var sessionsDir = Path.Combine(tmp, "sessions");
        Directory.CreateDirectory(sessionsDir);

        try
        {
            var sessionPath = Path.Combine(sessionsDir, "abc.jsonl");
            await File.WriteAllTextAsync(sessionPath, "{}");

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
            await agent.DeleteSessionAsync("abc");

            await Assert.That(File.Exists(sessionPath)).IsFalse();
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
