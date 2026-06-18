namespace PicoWeb;

public sealed class WebServerOptions
{
    public required IPEndPoint Endpoint { get; init; }

    public SslServerAuthenticationOptions? SslOptions { get; init; }

    /// <summary>
    /// When true and the endpoint is IPv6, the socket accepts both IPv6 and IPv4 connections.
    /// Defaults to true for seamless browser compatibility (modern browsers prefer IPv6 for localhost).
    /// </summary>
    public bool EnableDualMode { get; init; } = true;

    public ILogger? Logger { get; init; }
}
