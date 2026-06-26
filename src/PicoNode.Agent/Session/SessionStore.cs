namespace PicoNode.Agent;

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
    public static async Task SaveAsync(
        string path,
        SessionData data,
        CancellationToken ct = default
    )
    {
        var metaLine = Encoding.UTF8.GetBytes(
            $"{{\"$meta\":{{\"thinkingEnabled\":{data.ThinkingEnabled.ToString().ToLower()},\"thinkingLevel\":\"{data.ThinkingLevel.ToString().ToLower()}\"}}}}\n"
        );
        var messageLines = PicoJetson.JsonSerializer.SerializeLines(data.Messages);
        var combined = new byte[metaLine.Length + messageLines.Length];
        Buffer.BlockCopy(metaLine, 0, combined, 0, metaLine.Length);
        Buffer.BlockCopy(messageLines, 0, combined, metaLine.Length, messageLines.Length);
        await File.WriteAllBytesAsync(path, combined, ct);
    }

    public static async Task<SessionData> LoadAsync(string path, CancellationToken ct = default)
    {
        var result = new SessionData();
        if (!File.Exists(path))
            return result;

        var bytes = await File.ReadAllBytesAsync(path, ct);

        // Parse $meta line
        var firstNewline = Array.IndexOf(bytes, (byte)'\n');
        if (firstNewline > 0)
        {
            var firstLine = Encoding.UTF8.GetString(bytes, 0, firstNewline);
            if (firstLine.StartsWith("{\"$meta\"", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(firstLine);
                    if (doc.RootElement.TryGetProperty("$meta", out var meta))
                    {
                        if (meta.TryGetProperty("thinkingEnabled", out var te))
                            result.ThinkingEnabled = te.GetBoolean();
                        if (meta.TryGetProperty("thinkingLevel", out var tl))
                            result.ThinkingLevel = Enum.TryParse<ThinkingLevel>(
                                tl.GetString(),
                                ignoreCase: true,
                                out var l
                            )
                                ? l
                                : ThinkingLevel.Medium;
                    }
                }
                catch
                { /* ignore malformed meta */
                }

                bytes = bytes[(firstNewline + 1)..];
            }
        }

        var entries = PicoJetson.JsonSerializer.DeserializeLines<Message>(bytes);
        result.Messages = entries.Where(e => e != null).Select(e => e!).ToList();
        return result;
    }
}
