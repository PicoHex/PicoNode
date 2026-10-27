namespace PicoNode.Web;

public sealed class AuthOptions
{
    public required Func<
        string,
        CancellationToken,
        ValueTask<AuthIdentity?>
    > ValidateToken { get; init; }

    /// <summary>
    /// Optional logger for token-validation failures. The middleware keeps its
    /// fail-open behaviour, but with a logger configured a throwing validator
    /// is reported as a warning instead of vanishing silently.
    /// </summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// When <c>true</c>, a request that ends up without an identity is rejected with
    /// <c>401 Unauthorized</c> instead of continuing anonymously — "registering the
    /// middleware protects the route". Defaults to <c>false</c> (fail-open), so existing
    /// hosts keep letting downstream code decide.
    /// </summary>
    /// <remarks>
    /// An identity is absent when the <c>Authorization</c> header is missing, is not a
    /// bearer token, carries an empty token, or when <see cref="ValidateToken"/> returns
    /// <c>null</c> or throws. Scope the middleware to the routes that need it
    /// (e.g. <c>/admin</c>) — with this flag on, it protects every request it sees.
    /// </remarks>
    public bool RequireIdentity { get; init; }
}
