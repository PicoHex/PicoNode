namespace PicoNode.Web;

public sealed class AuthOptions
{
    public required Func<string, CancellationToken, ValueTask<AuthIdentity?>> ValidateToken
    {
        get;
        init;
    }
}
