namespace PicoNode.Web;

public sealed class CorsOptions
{
    // Idempotent, so a per-instance flag is enough; the write races benignly (every
    // thread reaches the same verdict) which is why it is not locked.
    private volatile bool _validated;
    private string? _allowedMethodsHeader;
    private string? _allowedHeadersHeader;
    private string? _exposedHeadersHeader;
    private HashSet<string>? _allowedOriginsSet;
    private HashSet<string>? _allowedMethodsSet;
    private HashSet<string>? _allowedHeadersSet;

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedMethods { get; init; } =
    ["GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS"];

    public IReadOnlyList<string> AllowedHeaders { get; init; } =
    [HttpHeaderNames.ContentType, HttpHeaderNames.Authorization];

    public IReadOnlyList<string> ExposedHeaders { get; init; } = [];

    public bool AllowCredentials { get; init; }

    public int? MaxAge { get; init; }

    /// <summary>
    /// Rejects configurations that cannot mean what the caller intended. Runs once per
    /// instance, on first use by <see cref="CorsHandler"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty <see cref="AllowedOrigins"/> used to answer every cross-origin request with
    /// <c>403</c> and no diagnostic — a silent misconfiguration.
    /// </para>
    /// <para>
    /// <c>["*"]</c> combined with <see cref="AllowCredentials"/> used to reflect the request
    /// origin verbatim, so <b>every</b> site received <c>Access-Control-Allow-Origin</c> plus
    /// <c>Access-Control-Allow-Credentials: true</c>. The Fetch spec forbids a wildcard on a
    /// credentialed response and ASP.NET Core refuses to start on that pair; here it silently
    /// became universal, credentialed trust.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="AllowedOrigins"/> is empty, or it contains <c>"*"</c> while
    /// <see cref="AllowCredentials"/> is <c>true</c>.
    /// </exception>
    internal void Validate()
    {
        if (_validated)
            return;

        if (AllowedOrigins.Count == 0)
            throw new InvalidOperationException(
                "CORS is misconfigured: AllowedOrigins is empty, so every cross-origin request "
                    + "is rejected without any diagnostic. List the allowed origins explicitly, "
                    + "or use [\"*\"] for a public API that does not send credentials."
            );

        if (AllowCredentials && AllowedOriginsSet.Contains("*"))
            throw new InvalidOperationException(
                "CORS is misconfigured: AllowedOrigins contains \"*\" while AllowCredentials is "
                    + "true. The Fetch spec forbids a wildcard Access-Control-Allow-Origin on a "
                    + "credentialed response, and echoing the request origin instead would grant "
                    + "every site credentialed cross-origin access. List the origins that may "
                    + "send credentials explicitly."
            );

        _validated = true;
    }

    internal string AllowedMethodsHeader =>
        _allowedMethodsHeader ??= string.Join(", ", AllowedMethods);

    internal string AllowedHeadersHeader =>
        _allowedHeadersHeader ??= string.Join(", ", AllowedHeaders);

    internal string ExposedHeadersHeader =>
        _exposedHeadersHeader ??= string.Join(", ", ExposedHeaders);

    internal HashSet<string> AllowedOriginsSet =>
        _allowedOriginsSet ??= new HashSet<string>(
            AllowedOrigins,
            StringComparer.OrdinalIgnoreCase
        );

    internal HashSet<string> AllowedMethodsSet =>
        _allowedMethodsSet ??= new HashSet<string>(
            AllowedMethods,
            StringComparer.OrdinalIgnoreCase
        );

    internal HashSet<string> AllowedHeadersSet =>
        _allowedHeadersSet ??= new HashSet<string>(
            AllowedHeaders,
            StringComparer.OrdinalIgnoreCase
        );
}
