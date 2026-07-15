namespace PicoNode.Agent.Domain;

[PicoSerializable]
[JsonCamelCase]
public sealed class SessionListItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public int ParticipantCount { get; set; }
}

public static class SessionLister
{
    public static List<SessionListItem> List(string sessionsDir)
    {
        if (!Directory.Exists(sessionsDir))
            return [];

        var files = Directory.GetFiles(sessionsDir, "*.jsonl");
        var list = new List<SessionListItem>();
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var name = ReadSessionName(file);
            list.Add(
                new SessionListItem
                {
                    Id = id,
                    Name = name,
                    CreatedAt = File.GetCreationTimeUtc(file).ToString("O"),
                    ParticipantCount = 0,
                }
            );
        }
        return list;
    }

    private static string ReadSessionName(string jsonlPath)
    {
        try
        {
            using var reader = new StreamReader(jsonlPath, Encoding.UTF8);
            var firstLine = reader.ReadLine();
            if (firstLine is null)
                return Path.GetFileNameWithoutExtension(jsonlPath);

            var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(firstLine));
            if (doc.RootElement.TryGetProperty("Name", out var nameProp))
            {
                var name = nameProp.GetStringOrNull();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }

            return Path.GetFileNameWithoutExtension(jsonlPath);
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(jsonlPath);
        }
    }

    /// <summary>
    /// Same as List() but returns pre-serialized JSON.
    /// Serialization lives in PicoNode.Agent.Core so the source generator
    /// can handle List&lt;SessionListItem&gt; (cross-project generic collections
    /// are not visible to PicoJetson in the consuming project).
    /// </summary>
    public static string ListJson(string sessionsDir)
    {
        var list = List(sessionsDir);
        return JsonSerializer.Serialize(list);
    }
}
