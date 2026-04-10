namespace PicoNode.Web;

public static class CookieParser
{
    public static IReadOnlyDictionary<string, string> Parse(
        IReadOnlyList<KeyValuePair<string, string>> headers
    )
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            if (!header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ParseCookieHeader(header.Value, cookies);
        }

        return cookies;
    }

    public static IReadOnlyDictionary<string, string> Parse(string cookieHeaderValue)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ParseCookieHeader(cookieHeaderValue, cookies);
        return cookies;
    }

    private static void ParseCookieHeader(string value, Dictionary<string, string> cookies)
    {
        var remaining = value.AsSpan();

        while (remaining.Length > 0)
        {
            var semicolon = remaining.IndexOf(';');
            var pair = semicolon >= 0 ? remaining[..semicolon] : remaining;

            var equals = pair.IndexOf('=');
            if (equals > 0)
            {
                var name = pair[..equals].Trim();
                var cookieValue = pair[(equals + 1)..].Trim();

                if (name.Length > 0)
                {
                    cookies.TryAdd(name.ToString(), cookieValue.ToString());
                }
            }

            if (semicolon < 0)
            {
                break;
            }

            remaining = remaining[(semicolon + 1)..];
        }
    }
}
