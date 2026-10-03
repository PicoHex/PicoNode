namespace PicoNode.Web;

/// <summary>
/// Bearer token authentication middleware.
/// Extracts the token from the <c>Authorization</c> header and calls
/// <see cref="AuthOptions.ValidateToken"/> to resolve an identity.
/// The identity is stored in <see cref="WebContext.Items"/> under
/// <see cref="WebContextKeys.AuthIdentity"/>.
/// </summary>
public sealed class AuthMiddleware
{
    /// <summary>
    /// Creates a Bearer token authentication middleware. Token validation
    /// failures are fail-open: the request continues unauthenticated, and
    /// downstream code must check <see cref="GetIdentity"/> before granting
    /// access (the middleware never rejects a request by itself).
    /// </summary>
    public static WebMiddleware Create(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ValidateToken);

        return async (context, next, ct) =>
        {
            if (TryGetBearerToken(context, out var token))
            {
                try
                {
                    var identity = await options.ValidateToken(token, ct);
                    if (identity is not null)
                        context.Items[WebContextKeys.AuthIdentity] = identity;
                }
                catch (Exception ex)
                {
                    // Validation failed — identity not injected.
                    // Downstream decides whether anonymous access is allowed;
                    // log so a broken validator is observable (fail-open).
                    options.Logger?.Log(
                        LogLevel.Warning,
                        new EventId(0),
                        "Bearer token validation failed; request continues unauthenticated",
                        ex
                    );
                }
            }

            return await next(context, ct);
        };
    }

    /// <summary>
    /// Tries to extract the bearer token from the request's <c>Authorization</c> header,
    /// reproducing the parse this middleware used inline before it was shared with the
    /// rate-limit <see cref="RateLimitKeys.TokenMatch"/> classifier: <c>Split(' ', 2)</c>,
    /// a <see cref="StringComparison.OrdinalIgnoreCase"/> <c>"Bearer"</c> scheme, an empty
    /// raw value rejected, and the token taken up to the first comma with <b>no</b> trim.
    /// </summary>
    /// <param name="ctx">The request context.</param>
    /// <param name="token">
    /// The extracted token, or <see cref="string.Empty"/> when this returns <c>false</c>
    /// (never null, so the non-nullable <c>out string</c> always holds).
    /// </param>
    /// <returns><c>true</c> when a non-empty bearer token was extracted.</returns>
    internal static bool TryGetBearerToken(WebContext ctx, out string token)
    {
        token = string.Empty;

        if (!ctx.Request.Headers.TryGetValue("Authorization", out var header))
            return false;

        var parts = header.Split(' ', 2);
        if (
            parts.Length != 2
            || !string.Equals(parts[0], "Bearer", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        var raw = parts[1];
        if (raw.Length == 0)
            return false;

        var comma = raw.IndexOf(',');
        token = comma >= 0 ? raw[..comma] : raw;
        return true;
    }

    /// <summary>
    /// Retrieves the authenticated identity from the context, or null if not authenticated.
    /// </summary>
    public static AuthIdentity? GetIdentity(WebContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (
            context.Items.TryGetValue(WebContextKeys.AuthIdentity, out var v)
            && v is AuthIdentity identity
        )
            return identity;

        return null;
    }
}
