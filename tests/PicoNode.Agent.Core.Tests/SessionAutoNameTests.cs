using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class SessionAutoNameTests
{
    [Test]
    public async Task DefaultSession_NameCanBeUpdated()
    {
        var storage = new InMemorySessionStorage();
        var session = new Session(Guid.CreateVersion7(), storage: storage);
        await Assert.That(session.Name).IsEqualTo("default");

        await storage.SetName("Debug Session");
        await Assert.That(storage.Name).IsEqualTo("Debug Session");
    }

    [Test]
    public async Task JsonlSession_NameRoundTrips()
    {
        var baseDir = Path.Combine(
            Path.GetTempPath(), $"pico-autoname-{Guid.CreateVersion7():N}");
        try
        {
            var sessionId = Guid.CreateVersion7();
            var storage = new JsonlSessionStorage(sessionId, baseDir: baseDir);

            await storage.SetName("My Chat");

            var storage2 = new JsonlSessionStorage(sessionId, baseDir: baseDir);
            await Assert.That(storage2.Name).IsEqualTo("My Chat");
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }
}
