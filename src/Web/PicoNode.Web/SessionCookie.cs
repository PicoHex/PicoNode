namespace PicoNode.Web;

public static class SessionCookie
{
    public static (SessionIdExtractor Extract, SessionIdSetter Set) Create(
        string cookieName = "sid"
    )
    {
        return (
            Extract: request =>
            {
                if (!request.Headers.TryGetValue("Cookie", out var cookieHeader))
                    return null;

                foreach (var part in cookieHeader.Split(';'))
                {
                    var trimmed = part.Trim();
                    var eq = trimmed.IndexOf('=');
                    if (eq < 0)
                        continue;

                    var name = trimmed[..eq].Trim();
                    if (!string.Equals(name, cookieName, StringComparison.Ordinal))
                        continue;

                    var value = trimmed[(eq + 1)..].Trim();
                    return value.Length > 0 ? value : null;
                }

                return null;
            },
            Set: (response, sessionId) =>
            {
                var cookie = new SetCookieBuilder(cookieName, sessionId)
                    .Path("/")
                    .HttpOnly()
                    .SameSite("Lax")
                    .Build();
                response.Headers.Add(cookie.Key, cookie.Value);
            }
        );
    }
}
