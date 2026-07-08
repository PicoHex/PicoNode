namespace PicoNode.AI;

/// <summary>
/// Resolves a ThinkingLevel to a provider-specific API parameter value.
/// Strategy: exact match → nearest downward (cheaper) → nearest upward → null.
/// </summary>
public static class MapResolver
{
    private static readonly ThinkingLevel[] Ordered =
    [
        ThinkingLevel.Minimal,
        ThinkingLevel.Low,
        ThinkingLevel.Medium,
        ThinkingLevel.High,
        ThinkingLevel.XHigh,
    ];

    public static string? Resolve(ThinkingLevel requested, Dictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
            return null;

        // 1. Exact match
        var key = requested.ToString().ToLower();
        if (map.TryGetValue(key, out var value))
            return value;

        // 2. Downward (cheaper)
        for (int i = (int)requested - 1; i >= 0; i--)
        {
            key = Ordered[i].ToString().ToLower();
            if (map.TryGetValue(key, out value))
                return value;
        }

        // 3. Upward
        for (int i = (int)requested + 1; i < Ordered.Length; i++)
        {
            key = Ordered[i].ToString().ToLower();
            if (map.TryGetValue(key, out value))
                return value;
        }

        return null;
    }
}
