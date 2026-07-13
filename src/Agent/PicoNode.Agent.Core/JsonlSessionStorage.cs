using PicoJetson;

namespace PicoNode.Agent.Domain;

public sealed class JsonlSessionStorage : ISessionStorage
{
    private readonly InMemorySessionStorage _memory = new();
    private readonly string _jsonlPath;

    public JsonlSessionStorage(Guid sessionId, string baseDir = "data/sessions")
    {
        _jsonlPath = Path.Combine(baseDir, $"{sessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(_jsonlPath)!);
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_jsonlPath))
            return;

        var lines = File.ReadAllLines(_jsonlPath);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var entry = JsonSerializer.Deserialize<SessionTreeEntryBase>(
                Encoding.UTF8.GetBytes(line)
            );
            if (entry is not null)
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
