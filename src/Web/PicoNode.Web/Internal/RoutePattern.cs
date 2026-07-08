namespace PicoNode.Web.Internal;

internal sealed class RoutePattern
{
    private RoutePattern(bool hasParameters)
    {
        IsExact = !hasParameters;
    }

    internal bool IsExact { get; }

    internal static RoutePattern Parse(string pattern)
    {
        if (pattern == "/")
        {
            return new RoutePattern(false);
        }

        var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var hasParameters = false;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            if (part.StartsWith('{') && part.EndsWith('}') && part.Length > 2)
            {
                hasParameters = true;
                break;
            }
        }

        return new RoutePattern(hasParameters);
    }
}
