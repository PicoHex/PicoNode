namespace PicoNode.Web;

public sealed class MultipartFormDataParserOptions
{
    public const int DefaultMaxBoundaryLength = 70;

    public int MaxBoundaryLength { get; init; } = DefaultMaxBoundaryLength;
}
