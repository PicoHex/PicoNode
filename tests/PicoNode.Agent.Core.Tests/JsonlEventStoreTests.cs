using PicoNode.Actor;
using PicoNode.Actor.Abs;
using PicoNode.Agent.Domain;

namespace PicoNode.Agent.Core.Tests;

public sealed class JsonlEventStoreTests : IDisposable
{
    private readonly string _baseDir;

    public JsonlEventStoreTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"pico-test-{Guid.CreateVersion7():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    [Test]
    public async Task AppendThenLoad_RoundTrips()
    {
        var store = new JsonlEventStore(_baseDir);
        var actorId = Guid.CreateVersion7();
        var events = new List<DomainEvent>
        {
            new AgentCreated(
                [
                    new Llm
                    {
                        ProviderName = "x",
                        ModelId = "y",
                        ApiKey = "k",
                    },
                ],
                "x",
                "y",
                Guid.CreateVersion7(),
                null,
                null
            ),
            new AgentStarted(),
            new ToolAdded(new Tool { Name = "bash" }),
        };

        await store.AppendAsync(actorId, 0, events.Cast<IDomainEvent>().ToList());
        var loaded = await store.LoadAsync(actorId);

        await Assert.That(loaded.Count).IsEqualTo(3);
        await Assert.That(loaded[0]).IsTypeOf<AgentCreated>();
        await Assert.That(loaded[1]).IsTypeOf<AgentStarted>();
        await Assert.That(loaded[2]).IsTypeOf<ToolAdded>();
    }

    [Test]
    public async Task Concurrency_Throws()
    {
        var store = new JsonlEventStore(_baseDir);
        var actorId = Guid.CreateVersion7();
        var events = new[] { new AgentStarted() };

        await store.AppendAsync(actorId, 0, [events[0]]);
        // Second append with same expectedVersion should fail
        await Assert
            .That(async () => await store.AppendAsync(actorId, 0, [events[0]]))
            .Throws<ConcurrencyException>();
    }

    [Test]
    public async Task Load_EmptyFile_ReturnsEmpty()
    {
        var store = new JsonlEventStore(_baseDir);
        var result = await store.LoadAsync(Guid.CreateVersion7());
        await Assert.That(result.Count).IsEqualTo(0);
    }
}
