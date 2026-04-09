namespace PicoNode.Http;

public sealed class CorsOptions
{
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedMethods { get; init; } =
        ["GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS"];

    public IReadOnlyList<string> AllowedHeaders { get; init; } = ["Content-Type", "Authorization"];

    public IReadOnlyList<string> ExposedHeaders { get; init; } = [];

    public bool AllowCredentials { get; init; }

    public int? MaxAge { get; init; }
}
