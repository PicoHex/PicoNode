using PicoJetson;

namespace PicoNode.Agent.Domain;

public sealed class JsonlSessionStorage : ISessionStorage
{
    private readonly InMemorySessionStorage _memory = new();
    private readonly string _jsonlPath;

    public string Name { get; private set; }

    public JsonlSessionStorage(
        Guid sessionId,
        string? name = null,
        string baseDir = "data/sessions"
    )
    {
        _jsonlPath = Path.Combine(baseDir, $"{sessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(_jsonlPath)!);
        Name = name ?? "default";

        if (!File.Exists(_jsonlPath))
        {
            // New session — write name as first entry
            var info = new SessionInfoEntry { Name = Name };
            var json = JsonSerializer.Serialize((SessionTreeEntryBase)info);
            File.WriteAllText(_jsonlPath, json + Environment.NewLine);
        }

        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_jsonlPath))
            return;

        var lines = File.ReadAllLines(_jsonlPath);

        // Restore name from first SessionInfoEntry in raw lines
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var entry = JsonSerializer.Deserialize<SessionTreeEntryBase>(
                Encoding.UTF8.GetBytes(line)
            );
            if (entry is SessionInfoEntry si && si.Name is not null)
            {
                Name = si.Name;
                break;
            }
        }

        // Load all non-metadata entries into memory
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var entry = JsonSerializer.Deserialize<SessionTreeEntryBase>(
                Encoding.UTF8.GetBytes(line)
            );
            if (entry is not null && entry is not SessionInfoEntry)
                _memory.AppendEntry(entry).GetAwaiter().GetResult();
        }

        // Find last LeafEntry for current leaf position
        var entries = _memory.GetEntries().GetAwaiter().GetResult();

        for (var i = entries.Length - 1; i >= 0; i--)
        {
            if (entries[i] is LeafEntry leaf)
            {
                if (leaf.TargetId is not null)
                    _memory.MoveTo(leaf.TargetId).GetAwaiter().GetResult();
                return;
            }
        }
        // No LeafEntry — use last entry as leaf
        if (entries.Length > 0)
            _memory.MoveTo(entries[^1].Id).GetAwaiter().GetResult();
    }

    public Task<string> CreateEntryId() => _memory.CreateEntryId();

    public async Task AppendEntry(SessionTreeEntryBase entry)
    {
        await _memory.AppendEntry(entry);
        var json = JsonSerializer.Serialize(entry);
        await File.AppendAllTextAsync(_jsonlPath, json + Environment.NewLine);
    }

    public Task<string?> GetLeafId() => _memory.GetLeafId();

    public Task<SessionTreeEntryBase[]> GetEntries() => _memory.GetEntries();

    public Task<SessionTreeEntryBase[]> GetPathToRoot(string leafId) =>
        _memory.GetPathToRoot(leafId);

    public Task<SessionTreeEntryBase?> GetEntry(string id) => _memory.GetEntry(id);

    public Task<string?> GetLabel(string id) => _memory.GetLabel(id);

    public async Task SetName(string name)
    {
        await _memory.SetName(name);
        // Rewrite file with updated name in SessionInfoEntry
        var entries = await _memory.GetEntries();
        var info = JsonSerializer.Serialize((SessionTreeEntryBase)
            new SessionInfoEntry { Name = name });
        var json = info + Environment.NewLine + string.Join(
            Environment.NewLine,
            entries.Where(e => e is not LeafEntry).Select(e => JsonSerializer.Serialize(e))
        );
        // Keep last LeafEntry
        var lastLeaf = entries.LastOrDefault(e => e is LeafEntry);
        if (lastLeaf is not null)
            json += Environment.NewLine + JsonSerializer.Serialize(lastLeaf);
        await File.WriteAllTextAsync(_jsonlPath, json + Environment.NewLine);
    }

    public async Task MoveTo(string entryId)
    {
        await _memory.MoveTo(entryId);

        // Rewrite file without old LeafEntries, then append new one
        var entries = await _memory.GetEntries();
        var json = string.Join(
            Environment.NewLine,
            entries.Where(e => e is not LeafEntry).Select(e => JsonSerializer.Serialize(e))
        );
        var leaf = JsonSerializer.Serialize(
            (SessionTreeEntryBase)new LeafEntry { TargetId = entryId }
        );
        await File.WriteAllTextAsync(
            _jsonlPath,
            json + Environment.NewLine + leaf + Environment.NewLine
        );
    }
}
