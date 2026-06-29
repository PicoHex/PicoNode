using System.Text;
using SystemTextJson = System.Text.Json;

namespace PicoNode.Agent;

public sealed class JsonlSessionStorage : ISessionStorage, IAsyncDisposable
{
    private static readonly SystemTextJson.JsonSerializerOptions _jsonOptions = SessionJsonContext.Default.Options;

    private readonly string _filePath;
    private readonly List<SessionTreeEntryBase> _entries = [];
    private readonly Dictionary<string, SessionTreeEntryBase> _byId = [];
    private readonly Dictionary<string, string> _labelsById = [];
    private string? _leafId;
    private Func<string> _idGenerator;
    private bool _disposed;

    private JsonlSessionStorage(string filePath,
        List<SessionTreeEntryBase> entries, string? leafId)
    {
        _filePath = filePath;
        _entries = entries;
        foreach (var e in entries) _byId[e.Id] = e;
        _leafId = leafId;
        _idGenerator = DefaultIdGenerator;
    }

    internal void SetEntryIdGenerator(Func<string> generator) => _idGenerator = generator;

    public static async Task<JsonlSessionStorage> OpenAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new SessionException(SessionErrorCode.NotFound, $"Session file not found: {filePath}");

        var content = await File.ReadAllTextAsync(filePath);
        var lines = content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        if (lines.Length == 0)
            throw new SessionException(SessionErrorCode.InvalidSession, "Empty session file");

        var entries = new List<SessionTreeEntryBase>();
        string? leafId = null;
        for (int i = 1; i < lines.Length; i++)
        {
            var entry = SystemTextJson.JsonSerializer.Deserialize(lines[i], SessionJsonContext.Default.SessionTreeEntryBase)
                ?? throw new SessionException(SessionErrorCode.InvalidEntry, $"Invalid entry at line {i + 1}");
            entries.Add(entry);
            leafId = entry is LeafEntry lf ? lf.TargetId : entry.Id;
        }
        return new JsonlSessionStorage(filePath, entries, leafId);
    }

    public static async Task<JsonlSessionStorage> CreateAsync(string filePath, string sessionId)
    {
        var header = $"{{\"type\":\"session\",\"version\":3,\"id\":\"{sessionId}\",\"timestamp\":\"{DateTime.UtcNow:O}\"}}\n";
        await File.WriteAllTextAsync(filePath, header);
        return new JsonlSessionStorage(filePath, [], null);
    }

    public Task<string?> GetLeafId() => Task.FromResult(_leafId);

    public Task SetLeafId(string? leafId)
    {
        if (leafId is not null && !_byId.ContainsKey(leafId))
            throw new SessionException(SessionErrorCode.NotFound, $"Entry {leafId} not found");
        _leafId = leafId;
        return Task.CompletedTask;
    }

    public Task<string> CreateEntryId() => Task.FromResult(_idGenerator());

    public async Task AppendEntry(SessionTreeEntryBase entry)
    {
        var line = SystemTextJson.JsonSerializer.Serialize(entry, SessionJsonContext.Default.SessionTreeEntryBase) + "\n";
        await File.AppendAllTextAsync(_filePath, line);
        _entries.Add(entry);
        _byId[entry.Id] = entry;

        if (entry is LabelEntry { Label: { } label } le)
            _labelsById[le.TargetId] = label;
        else if (entry is LabelEntry le2)
            _labelsById.Remove(le2.TargetId);

        _leafId = entry is LeafEntry lf ? lf.TargetId : entry.Id;
    }

    public Task<SessionTreeEntryBase?> GetEntry(string id) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<SessionTreeEntryBase[]> GetPathToRoot(string? leafId)
    {
        if (leafId is null) return Task.FromResult(Array.Empty<SessionTreeEntryBase>());
        var path = new List<SessionTreeEntryBase>();
        var current = _byId.GetValueOrDefault(leafId)
            ?? throw new SessionException(SessionErrorCode.NotFound, $"Entry {leafId} not found");
        while (true)
        {
            path.Insert(0, current);
            if (current.ParentId is null) break;
            current = _byId.GetValueOrDefault(current.ParentId)
                ?? throw new SessionException(SessionErrorCode.InvalidEntry, $"Parent {current.ParentId} not found");
        }
        return Task.FromResult(path.ToArray());
    }

    public Task<SessionTreeEntryBase[]> GetEntries() =>
        Task.FromResult(_entries.ToArray());

    public Task<string?> GetLabel(string id) =>
        Task.FromResult(_labelsById.GetValueOrDefault(id));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_leafId is not null)
        {
            var leaf = new LeafEntry
            {
                Id = _idGenerator(),
                ParentId = _entries.Count > 0 ? _entries[^1].Id : null,
                Timestamp = DateTime.UtcNow.ToString("O"),
                TargetId = _leafId,
            };
            await AppendEntry(leaf);
        }
    }

    public static async Task SaveAsync(string path,
        IReadOnlyList<SessionTreeEntryBase> entries,
        CancellationToken ct = default)
    {
        var header = $"{{\"type\":\"session\",\"version\":3,\"timestamp\":\"{DateTime.UtcNow:O}\"}}\n";
        var sb = new StringBuilder();
        sb.Append(header);
        foreach (var entry in entries)
            sb.Append(SystemTextJson.JsonSerializer.Serialize(entry, SessionJsonContext.Default.SessionTreeEntryBase)).Append('\n');
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private static string DefaultIdGenerator()
    {
        return Guid.CreateVersion7().ToString("N")[..8];
    }
}
