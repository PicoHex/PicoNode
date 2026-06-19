namespace PicoNode.Http;

/// <summary>
/// Centralized HTTP header name constants to avoid string duplication across projects.
/// </summary>
public static class HttpHeaderNames
{
    public const string ContentLength = "Content-Length";
    public const string ContentType = "Content-Type";
    public const string Connection = "Connection";
    public const string TransferEncoding = "Transfer-Encoding";
    public const string Server = "Server";
    public const string Host = "Host";
    public const string ContentEncoding = "Content-Encoding";
    public const string AcceptEncoding = "Accept-Encoding";
    public const string Vary = "Vary";
    public const string SetCookie = "Set-Cookie";
    public const string Location = "Location";
    public const string Expect = "Expect";
    public const string Authorization = "Authorization";
    public const string Origin = "Origin";
    public const string AccessControlRequestMethod = "Access-Control-Request-Method";
    public const string AccessControlAllowOrigin = "Access-Control-Allow-Origin";
    public const string AccessControlAllowMethods = "Access-Control-Allow-Methods";
    public const string AccessControlAllowHeaders = "Access-Control-Allow-Headers";
    public const string AccessControlAllowCredentials = "Access-Control-Allow-Credentials";
    public const string AccessControlMaxAge = "Access-Control-Max-Age";
    public const string AccessControlExposeHeaders = "Access-Control-Expose-Headers";
}
