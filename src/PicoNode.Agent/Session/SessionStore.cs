namespace PicoNode.Agent;

using PicoNode.AI;

/// <summary>Triggers PicoJetson SG for types from PicoNode.AI used in this assembly.</summary>
internal static class SerializerInit
{
    static SerializerInit()
    {
        _ = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(new Message());
        _ = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(new ContentBlock());
        _ = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(new TokenUsage());
    }
}

public static class SessionStore
{
    public static async Task SaveAsync(string path, List<Message> messages, CancellationToken ct = default)
    {
        var jsonl = PicoJetson.JsonSerializer.SerializeLines(messages);
        await File.WriteAllBytesAsync(path, jsonl, ct);
    }

    public static async Task<List<Message>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return [];

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var entries = PicoJetson.JsonSerializer.DeserializeLines<Message>(bytes);
        return entries.Where(e => e != null).Select(e => e!).ToList();
    }
}
