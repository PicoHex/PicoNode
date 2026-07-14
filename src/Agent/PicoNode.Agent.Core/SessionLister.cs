namespace PicoNode.Agent.Domain;

public sealed record SessionListItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public int ParticipantCount { get; init; }
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
            list.Add(new SessionListItem
            {
                Id = id,
                Name = id,
                CreatedAt = File.GetCreationTimeUtc(file).ToString("O"),
                ParticipantCount = 0,
            });
        }
        return list;
    }
}
