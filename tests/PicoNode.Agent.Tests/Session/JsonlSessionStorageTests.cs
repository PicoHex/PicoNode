using PicoNode.Agent;

namespace PicoNode.Agent.Tests.Session;

public class JsonlSessionStorageTests
{
    private string _tempFile = null!;

    [Before(Test)]
    public void Setup()
    {
        _tempFile = Path.GetTempFileName();
    }

    [After(Test)]
    public void Cleanup()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Test]
    public async Task Create_And_Reopen_ShouldRestoreEntries()
    {
        var created = await JsonlSessionStorage.CreateAsync(_tempFile, Guid.NewGuid().ToString("N"));
        created.SetEntryIdGenerator(() => "test0001");
        var entry = new MessageEntry
        {
            Id = "test0001",
            Message = new Message { Role = "user", Content = "hello" },
        };
        await created.AppendEntry(entry);
        await created.DisposeAsync();

        var opened = await JsonlSessionStorage.OpenAsync(_tempFile);
        var entries = await opened.GetEntries();
        await Assert.That(entries).HasCount().EqualTo(2); // message + leaf
    }

    [Test]
    public async Task AppendEntry_ShouldWriteToDisk()
    {
        var storage = await JsonlSessionStorage.CreateAsync(_tempFile, Guid.NewGuid().ToString("N"));
        storage.SetEntryIdGenerator(() => "test0001");
        var entry = new MessageEntry
        {
            Id = "test0001",
            Message = new Message { Role = "user", Content = "hello" },
        };
        await storage.AppendEntry(entry);

        var content = await File.ReadAllTextAsync(_tempFile);
        await Assert.That(content).Contains("test0001");
    }
}
