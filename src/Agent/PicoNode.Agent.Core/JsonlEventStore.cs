using System.Text;
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

        var list = new List<IDomainEvent>();
        // Read line-by-line so a single unknown $type doesn't corrupt the entire stream.
        using var reader = File.OpenText(path);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var e = DomainEventSerializer.Deserialize(Encoding.UTF8.GetBytes(line));
                list.Add(e);
            }
            catch (FormatException ex) when (ex.Message.Contains("$type"))
            {
                // Skip events from future code versions with unknown type discriminators.
                System.Diagnostics.Debug.WriteLine(
                    $"Skipping unknown event in {path}: {ex.Message}"
                );
            }
        }
        return list;
    }
}
