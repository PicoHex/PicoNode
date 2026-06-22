namespace PicoNode.Web.Session.Abs;

public interface ISession
{
    string Id { get; }

    bool IsNew { get; }

    bool IsDirty { get; }

    bool TryGetValue(string key, out byte[]? value);

    void SetValue(string key, byte[] value);

    void Remove(string key);

    void Clear();

    IEnumerable<string> Keys { get; }
}
