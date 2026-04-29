namespace PicoNode.Web;

public sealed class CorsOptions
{
    private string? _allowedMethodsHeader;
    private string? _allowedHeadersHeader;
    private string? _exposedHeadersHeader;
    private HashSet<string>? _allowedOriginsSet;
    private HashSet<string>? _allowedMethodsSet;
    private HashSet<string>? _allowedHeadersSet;

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedMethods { get; init; } =
        ["GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS"];

    public IReadOnlyList<string> AllowedHeaders { get; init; } = ["Content-Type", "Authorization"];

    public IReadOnlyList<string> ExposedHeaders { get; init; } = [];

    public bool AllowCredentials { get; init; }

    public int? MaxAge { get; init; }

    internal string AllowedMethodsHeader =>
        _allowedMethodsHeader ??= string.Join(", ", AllowedMethods);

    internal string AllowedHeadersHeader =>
        _allowedHeadersHeader ??= string.Join(", ", AllowedHeaders);

    internal string ExposedHeadersHeader =>
        _exposedHeadersHeader ??= string.Join(", ", ExposedHeaders);

    internal HashSet<string> AllowedOriginsSet =>
        _allowedOriginsSet ??= new HashSet<string>(AllowedOrigins, StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> AllowedMethodsSet =>
        _allowedMethodsSet ??= new HashSet<string>(AllowedMethods, StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> AllowedHeadersSet =>
        _allowedHeadersSet ??= new HashSet<string>(AllowedHeaders, StringComparer.OrdinalIgnoreCase);
}
