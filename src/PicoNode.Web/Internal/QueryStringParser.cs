namespace PicoNode.Web.Internal;

internal static class QueryStringParser
{
    internal static Dictionary<string, string> Parse(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(queryString))
        {
            return result;
        }

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
