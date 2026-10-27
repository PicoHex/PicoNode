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
    /// <remarks>
    /// When this is on (and <see cref="Path"/> is <c>/</c>) the emitted name carries the
    /// <c>__Host-</c> prefix, which pins the cookie to the exact origin — a subdomain cannot
    /// set or overwrite it. Enabling <c>Secure</c> never happens by itself: a <c>__Host-</c>
    /// cookie without <c>Secure</c> is rejected by browsers, and forcing <c>Secure</c> would
    /// silently break every plain-HTTP deployment (decision D1).
    /// </remarks>
    public bool Secure { get; init; }

    /// <summary><c>SameSite</c> attribute value. Defaults to <c>Lax</c>.</summary>
    public string SameSite { get; init; } = "Lax";
}

public static class SessionCookie
{
    private const string HostPrefix = "__Host-";

    public static (SessionIdExtractor Extract, SessionIdSetter Set) Create(
        string cookieName = "sid"
    ) => Create(new SessionCookieOptions { CookieName = cookieName });

    public static (SessionIdExtractor Extract, SessionIdSetter Set) Create(
        SessionCookieOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        // Resolved once, so the reader and the writer cannot disagree about the name.
        var name = EffectiveName(options);

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

                    var partName = trimmed[..eq].Trim();
                    if (!string.Equals(partName, name, StringComparison.Ordinal))
                        continue;

                    var value = trimmed[(eq + 1)..].Trim();
                    return value.Length > 0 ? value : null;
                }

                return null;
            },
            Set: (response, sessionId) =>
            {
                var builder = new SetCookieBuilder(name, sessionId).Path(options.Path);

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

    /// <summary>
    /// The name actually used on the wire. The <c>__Host-</c> prefix is applied only when the
    /// cookie can legally carry it — <c>Secure</c> on and path <c>/</c> (there is no domain
    /// option, so there is no domain to violate). A browser drops a <c>__Host-</c> cookie that
    /// breaks either rule, i.e. the session cookie would vanish.
    /// </summary>
    /// <remarks>
    /// D1: the prefix rides on <see cref="SessionCookieOptions.Secure"/>; it never forces it.
    /// </remarks>
    internal static string EffectiveName(SessionCookieOptions options)
    {
        var name = options.CookieName;

        if (!options.Secure || !string.Equals(options.Path, "/", StringComparison.Ordinal))
            return name;

        return name.StartsWith(HostPrefix, StringComparison.Ordinal) ? name : HostPrefix + name;
    }
}
