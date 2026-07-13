using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class SessionNameTests
{
    [Test]
    public async Task Session_HasDefaultName()
    {
        var session = new Session(Guid.CreateVersion7(), "default", new InMemorySessionStorage());
        await Assert.That(session.Name).IsEqualTo("default");
    }

    [Test]
    public async Task JsonlSession_NameRoundTrips()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"pico-name-{Guid.CreateVersion7():N}");
        try
        {
            var sessionId = Guid.CreateVersion7();
            var storage = new JsonlSessionStorage(sessionId, "my-session", baseDir);
            await storage.AppendEntry(
                new MessageEntry
                {
                    Message = new Message { Role = "user", Content = "hi" },
                }
            );

            var storage2 = new JsonlSessionStorage(sessionId, null, baseDir);
            await Assert.That(storage2.Name).IsEqualTo("my-session");
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }
}
