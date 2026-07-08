namespace PicoNode.Http;

/// <summary>Represents the HTTP version used in requests and responses.</summary>
public enum HttpVersion
{
    /// <summary>HTTP/1.0</summary>
    Http10,

    /// <summary>HTTP/1.1 (default)</summary>
    Http11,

    /// <summary>HTTP/2</summary>
    Http2,
}
