namespace PicoNode.Web;

public sealed class AuthIdentity
{
    public required string UserId { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}
