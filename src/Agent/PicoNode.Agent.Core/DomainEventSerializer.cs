using PicoJetson;

namespace PicoNode.Agent.Domain;

/// <summary>
/// Exposes PicoSerDe polymorphic serialization for DomainEvent to test projects.
/// Avoids per-project SG scanning issues — all serialization generation stays in Agent.Core.
/// </summary>
public static class DomainEventSerializer
{
    public static string Serialize(DomainEvent e) => JsonSerializer.Serialize(e);

    public static DomainEvent Deserialize(byte[] json) =>
        JsonSerializer.Deserialize<DomainEvent>(json)!;
}
