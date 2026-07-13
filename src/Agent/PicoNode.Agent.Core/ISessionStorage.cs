namespace PicoNode.Agent.Domain;

public interface ISessionStorage
{
    string Name { get; }
    Task<string> CreateEntryId();
    Task AppendEntry(SessionTreeEntryBase entry);
    Task<string?> GetLeafId();
    Task<SessionTreeEntryBase[]> GetEntries();
    Task<SessionTreeEntryBase[]> GetPathToRoot(string leafId);
    Task<SessionTreeEntryBase?> GetEntry(string id);
    Task<string?> GetLabel(string id);
    Task MoveTo(string entryId);
    Task SetName(string name);
}
