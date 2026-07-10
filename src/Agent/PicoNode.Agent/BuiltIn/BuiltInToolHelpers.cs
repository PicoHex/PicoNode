namespace PicoNode.Agent;

internal static class BuiltInToolHelpers
{
    internal static string GetStringArg(IReadOnlyDictionary<string, object?> args, string key)
    {
        return args.TryGetValue(key, out var v) && v is string s ? s : "";
    }

    internal static long GetLongArg(
        IReadOnlyDictionary<string, object?> args,
        string key,
        long fallback
    )
    {
        if (!args.TryGetValue(key, out var v))
            return fallback;
        return v is long l ? l : fallback;
    }
}
