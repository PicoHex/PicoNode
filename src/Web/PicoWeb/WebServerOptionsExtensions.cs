using PicoNode;
using PicoNode.Abs;

namespace PicoWeb;

/// <summary>
/// Mapping helpers between the PicoWeb-layer option types and their
/// PicoNode counterparts. Centralizes the field-by-field copies so a new
/// option only needs to be added in one place (and here), not at every
/// call site.
/// </summary>
internal static class WebServerOptionsExtensions
{
    internal static TcpNodeOptions ToTcpNodeOptions(
        this WebServerOptions options,
        ITcpConnectionHandler connectionHandler
    ) =>
        new()
        {
            Endpoint = options.Endpoint,
            ConnectionHandler = connectionHandler,
            Logger = options.Logger,
            SslOptions = options.SslOptions,
            EnableDualMode = options.EnableDualMode,
            MaxConnections = options.MaxConnections,
            ReceiveSocketBufferSize = options.ReceiveSocketBufferSize,
            SendSocketBufferSize = options.SendSocketBufferSize,
            NoDelay = options.NoDelay,
            IdleTimeout = options.IdleTimeout,
            DrainTimeout = options.DrainTimeout,
            AcceptFaultBackoff = options.AcceptFaultBackoff,
        };
}
