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
    /// Creates a Bearer token authentication middleware.
    /// </summary>
    public static WebMiddleware Create(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ValidateToken);

        return async (context, next, ct) =>
        {
            if (context.Request.Headers.TryGetValue("Authorization", out var header))
            {
                var parts = header.Split(' ', 2);
                if (parts.Length == 2
                    && string.Equals(parts[0], "Bearer", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = parts[1];
                    if (raw.Length == 0)
                        return await next(context, ct);

                    var comma = raw.IndexOf(',');
                    var token = comma >= 0 ? raw[..comma] : raw;

                    try
                    {
                        var identity = await options.ValidateToken(token, ct);
                        if (identity is not null)
                            context.Items[WebContextKeys.AuthIdentity] = identity;
                    }
                    catch
                    {
                        // Validation failed — identity not injected.
                        // Downstream decides whether anonymous access is allowed.
                    }
                }
            }

            return await next(context, ct);
        };
    }

    /// <summary>
    /// Retrieves the authenticated identity from the context, or null if not authenticated.
    /// </summary>
    public static AuthIdentity? GetIdentity(WebContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(WebContextKeys.AuthIdentity, out var v)
            && v is AuthIdentity identity)
            return identity;

        return null;
    }
}
