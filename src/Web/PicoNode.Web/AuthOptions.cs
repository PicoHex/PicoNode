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
}
