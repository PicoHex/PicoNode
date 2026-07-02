using System.Text;

namespace PicoNode.Agent;

/// <summary>
/// Serializer for session entries using PicoJetson polymorphic serialization.
/// </summary>
internal static class SessionEntrySerializer
{
    public static string Serialize(SessionTreeEntryBase entry) =>
        PicoJetson.JsonSerializer.Serialize(entry);

    public static SessionTreeEntryBase? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return PicoJetson.JsonSerializer.Deserialize<SessionTreeEntryBase>(
            Encoding.UTF8.GetBytes(json)
        );
    }
}
