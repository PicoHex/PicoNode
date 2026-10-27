namespace PicoNode.Web;

public sealed class MultipartFormDataParserOptions
{
    public const int DefaultMaxBoundaryLength = 70;
    public const int DefaultMaxPartSizeBytes = 64 * 1024 * 1024; // 64 MB
    public const int DefaultMaxTotalSizeBytes = 64 * 1024 * 1024; // 64 MB
    public const int DefaultMaxPartCount = 256;
    public const int DefaultMaxHeaderBytesPerPart = 8 * 1024; // 8 KB

    public int MaxBoundaryLength { get; init; } = DefaultMaxBoundaryLength;

    /// <summary>
    /// Maximum size of a single part (content only). Enforced on both the
    /// in-memory and streaming parse paths.
    /// </summary>
    public int MaxPartSizeBytes { get; init; } = DefaultMaxPartSizeBytes;

    /// <summary>
    /// Maximum accumulated size of all parts (part headers plus part content).
    /// Enforced on both the in-memory and streaming parse paths.
    /// </summary>
    public int MaxTotalSizeBytes { get; init; } = DefaultMaxTotalSizeBytes;

    /// <summary>
    /// Maximum number of parts in the body. Enforced on both the in-memory and streaming
    /// parse paths. A part without content costs no size budget, so without this bound the
    /// number of parts — and the objects and strings each one becomes — is unbounded.
    /// </summary>
    public int MaxPartCount { get; init; } = DefaultMaxPartCount;

    /// <summary>
    /// Maximum wire size of a single part's header block. Enforced on both parse paths and
    /// counted toward <see cref="MaxTotalSizeBytes"/>.
    /// </summary>
    public int MaxHeaderBytesPerPart { get; init; } = DefaultMaxHeaderBytesPerPart;
}
