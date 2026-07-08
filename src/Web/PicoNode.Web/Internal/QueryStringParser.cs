namespace PicoNode.Web.Internal;

internal static class QueryStringParser
{
    private static readonly Dictionary<string, string> Empty = new(
        StringComparer.OrdinalIgnoreCase
    );

    internal static IReadOnlyDictionary<string, string> Parse(string queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return Empty;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var span = queryString.AsSpan();

        while (span.Length > 0)
        {
            var ampIndex = span.IndexOf('&');
            var pair = ampIndex >= 0 ? span[..ampIndex] : span;

            var eqIndex = pair.IndexOf('=');
            if (eqIndex >= 0)
            {
                var key = pair[..eqIndex];
                var value = pair[(eqIndex + 1)..];
                if (key.Length > 0)
                {
                    result.TryAdd(
                        Uri.UnescapeDataString(key.ToString()),
                        Uri.UnescapeDataString(value.ToString())
                    );
                }
            }
            else if (pair.Length > 0)
            {
                result.TryAdd(Uri.UnescapeDataString(pair.ToString()), string.Empty);
            }

            if (ampIndex < 0)
            {
                break;
            }

            span = span[(ampIndex + 1)..];
        }

        return result;
    }
}
