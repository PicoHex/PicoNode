namespace PicoAgent.Tests.Session;

using PicoNode.AI;

public class SessionStoreTests
{
    [Test]
    public async Task SgInit_ForceMessageSerializers()
    {
        var msg = new Message { Role = "user", Content = "x", Timestamp = 1 };
        var json = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(msg);
        var restored = PicoJetson.JsonSerializer.Deserialize<Message>(json);
        await Assert.That(restored!.Content).IsEqualTo("x");
    }

    [Test]
    public async Task SaveAndLoad_RoundTrip_PreservesMessages()
    {
        var messages = new List<Message>
        {
            new() { Role = "user", Content = "Hello", Timestamp = 1 },
            new()
            {
                Role = "assistant",
                ContentBlocks = [new ContentBlock { Type = "text", Text = "Hi!" }],
                StopReason = "end_turn",
                Timestamp = 2,
            },
        };

        var dir = Path.Combine(Path.GetTempPath(), "pico-session-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "test-session.jsonl");

        try
        {
            await SessionStore.SaveAsync(path, messages);

            await Assert.That(File.Exists(path)).IsTrue();

            var loaded = await SessionStore.LoadAsync(path);

            await Assert.That(loaded.Count).IsEqualTo(2);
            await Assert.That(loaded[0].Role).IsEqualTo("user");
            await Assert.That(loaded[0].Content).IsEqualTo("Hello");
            await Assert.That(loaded[1].ContentBlocks![0].Text).IsEqualTo("Hi!");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
