namespace PicoNode.Agent;

public interface ISessionStorage
{
    Task<string?> GetLeafId();
    Task SetLeafId(string? leafId);
    Task<string> CreateEntryId();
    Task AppendEntry(SessionTreeEntryBase entry);
    Task<SessionTreeEntryBase?> GetEntry(string id);
    Task<SessionTreeEntryBase[]> GetPathToRoot(string? leafId);
    Task<SessionTreeEntryBase[]> GetEntries();
    Task<string?> GetLabel(string id);
}
