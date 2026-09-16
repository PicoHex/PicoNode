namespace PicoNode.Web;

/// <summary>Options for the session cookie emitted by <see cref="SessionCookie"/>.</summary>
public sealed class SessionCookieOptions
{
    /// <summary>Cookie name. Defaults to <c>sid</c>.</summary>
    public string CookieName { get; init; } = "sid";

    /// <summary>Cookie path. Defaults to <c>/</c>.</summary>
    public string Path { get; init; } = "/";

    /// <summary>Emit the <c>HttpOnly</c> attribute. Defaults to on.</summary>
    public bool HttpOnly { get; init; } = true;

    /// <summary>
    /// Emit the <c>Secure</c> attribute. Off by default so plain-HTTP local
    /// development keeps working; enable it for HTTPS deployments.
    /// </summary>
    public bool Secure { get; init; }

    /// <summary><c>SameSite</c> attribute value. Defaults to <c>Lax</c>.</summary>
    public string SameSite { get; init; } = "Lax";
}

public static class SessionCookie
{
    public static (SessionIdExtractor Extract, SessionIdSetter Set) Create(
        string cookieName = "sid"
    ) => Create(new SessionCookieOptions { CookieName = cookieName });

    public static (SessionIdExtractor Extract, SessionIdSetter Set) Create(
        SessionCookieOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

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
                    if (!string.Equals(name, options.CookieName, StringComparison.Ordinal))
                        continue;

                    var value = trimmed[(eq + 1)..].Trim();
                    return value.Length > 0 ? value : null;
                }

                return null;
            },
            Set: (response, sessionId) =>
            {
                var builder = new SetCookieBuilder(options.CookieName, sessionId).Path(
                    options.Path
                );

                if (options.HttpOnly)
                    builder = builder.HttpOnly();
                if (options.Secure)
                    builder = builder.Secure();
                if (!string.IsNullOrEmpty(options.SameSite))
                    builder = builder.SameSite(options.SameSite);

                var cookie = builder.Build();
                response.Headers.Add(cookie.Key, cookie.Value);
            }
        );
    }
}
