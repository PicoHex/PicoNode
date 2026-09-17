namespace PicoNode.Web;

public sealed class MultipartFormDataParserOptions
{
    public const int DefaultMaxBoundaryLength = 70;
    public const int DefaultMaxPartSizeBytes = 64 * 1024 * 1024; // 64 MB
    public const int DefaultMaxTotalSizeBytes = 64 * 1024 * 1024; // 64 MB

    public int MaxBoundaryLength { get; init; } = DefaultMaxBoundaryLength;

    /// <summary>
    /// Maximum size of a single part (content only). Enforced on both the
    /// in-memory and streaming parse paths.
    /// </summary>
    public int MaxPartSizeBytes { get; init; } = DefaultMaxPartSizeBytes;

    /// <summary>
    /// Maximum accumulated size of all parts. Enforced on both the in-memory and
    /// streaming parse paths.
    /// </summary>
    public int MaxTotalSizeBytes { get; init; } = DefaultMaxTotalSizeBytes;
}
