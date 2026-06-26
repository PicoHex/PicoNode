namespace PicoNode.Agent.Tests.Session;

public sealed class SessionDataTests
{
    [Test]
    public async Task Save_and_load_preserves_thinking_state()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new SessionData
            {
                Messages =
                [
                    new Message
                    {
                        Role = "user",
                        Content = "Hello",
                        Timestamp = 1,
                    },
                ],
                ThinkingEnabled = true,
                ThinkingLevel = ThinkingLevel.High,
            };
            await SessionStore.SaveAsync(path, data);

            var loaded = await SessionStore.LoadAsync(path);
            await Assert.That(loaded.ThinkingEnabled).IsTrue();
            await Assert.That(loaded.ThinkingLevel).IsEqualTo(ThinkingLevel.High);
            await Assert.That(loaded.Messages.Count).IsEqualTo(1);
            await Assert.That(loaded.Messages[0].Content).IsEqualTo("Hello");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_old_format_no_meta_uses_defaults()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path,
                "{\"Role\":\"user\",\"Content\":\"Hi\",\"Timestamp\":1}\n"
            );

            var loaded = await SessionStore.LoadAsync(path);
            await Assert.That(loaded.ThinkingEnabled).IsTrue();
            await Assert.That(loaded.ThinkingLevel).IsEqualTo(ThinkingLevel.Medium);
            await Assert.That(loaded.Messages.Count).IsEqualTo(1);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
