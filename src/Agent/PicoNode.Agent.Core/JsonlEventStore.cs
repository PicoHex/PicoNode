using PicoJetson;
using PicoNode.Actor.Abs;

namespace PicoNode.Agent.Domain;

public sealed class JsonlEventStore : IEventStore
{
    private readonly string _baseDir;

    public JsonlEventStore(string baseDir = "data/actors")
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(_baseDir);
    }

    public async ValueTask<ulong> AppendAsync(
        Guid actorId,
        ulong expectedVersion,
        IReadOnlyList<IDomainEvent> events
    )
    {
        var path = Path.Combine(_baseDir, $"{actorId}.jsonl");
        var current = File.Exists(path) ? (ulong)(await File.ReadAllLinesAsync(path)).Length : 0;

        if (current != expectedVersion)
            throw new ConcurrencyException(actorId, expectedVersion, current);

        await using var writer = new StreamWriter(path, append: true);
        foreach (var e in events)
        {
            var json = DomainEventSerializer.Serialize((DomainEvent)e);
            await writer.WriteLineAsync(json);
        }
        return current + (ulong)events.Count;
    }

    public async ValueTask<IReadOnlyList<IDomainEvent>> LoadAsync(Guid actorId)
    {
        var path = Path.Combine(_baseDir, $"{actorId}.jsonl");
        if (!File.Exists(path))
            return Array.Empty<IDomainEvent>();

        await using var stream = File.OpenRead(path);
        var list = new List<IDomainEvent>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<DomainEvent>(
                stream,
                topLevelValues: true
            )
        )
        {
            list.Add(item!);
        }
        return list;
    }
}
