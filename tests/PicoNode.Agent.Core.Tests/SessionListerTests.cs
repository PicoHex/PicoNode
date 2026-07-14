namespace PicoNode.Agent.Tests;

public sealed class SessionListerTests
{
    [Test]
    public async Task List_ReturnsEmpty_WhenDirMissing()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pico-sess-{Guid.CreateVersion7():N}");
        try
        {
            var list = SessionLister.List(tmp);
            await Assert.That(list).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task List_ReturnsOneEntry_WhenSingleJsonlExists()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pico-sess-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var sessionId = Guid.CreateVersion7();
            await File.WriteAllTextAsync(Path.Combine(tmp, $"{sessionId}.jsonl"), "{}" + Environment.NewLine);

            var list = SessionLister.List(tmp);
            await Assert.That(list).HasSingleItem();
            await Assert.That(list[0].Id).IsEqualTo(sessionId.ToString());
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
