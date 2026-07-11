namespace PicoNode.AI;

/// <summary>
/// Shared PicoElement → Dictionary/List conversion helpers.
/// Used by SSE parsers and Agent.ParseSimpleJson to avoid duplication.
/// </summary>
internal static class PicoElementConverter
{
    public static Dictionary<string, object?> ObjectToDict(PicoElement el)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
            dict[prop.Name] = Convert(prop.Value);
        return dict;
    }

    public static object? Convert(PicoElement el)
    {
        return el.ValueKind switch
        {
            PicoValueKind.Object => ObjectToDict(el),
            PicoValueKind.Array => ArrayToList(el),
            PicoValueKind.String => el.GetString(),
            PicoValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            PicoValueKind.True => true,
            PicoValueKind.False => false,
            _ => null,
        };
    }

    public static List<object?> ArrayToList(PicoElement el)
    {
        var list = new List<object?>();
        foreach (var item in el.EnumerateArray())
            list.Add(Convert(item));
        return list;
    }
}
