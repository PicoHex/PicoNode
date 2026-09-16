using PicoNode.Http;

namespace PicoNode.Web;

/// <summary>
/// Mapping helpers between the Web-layer option types and their Http-layer
/// counterparts. Centralizes the field-by-field copies so a new option only
/// needs to be added in one place (and here), not at every call site.
/// </summary>
internal static class WebAppOptionsExtensions
{
    internal static HttpConnectionHandlerOptions ToHttpConnectionHandlerOptions(
        this WebAppOptions options,
        HttpRequestHandler requestHandler
    ) =>
        new()
        {
            RequestHandler = requestHandler,
            ServerHeader = options.ServerHeader,
            Logger = options.Logger,
            MaxRequestBytes = options.MaxRequestBytes,
            StreamingResponseBufferSize = options.StreamingResponseBufferSize,
            RequestTimeout = options.RequestTimeout,
            WebSocketMessageHandler = options.WebSocketMessageHandler,
            WebSocketMaxMessageSize = options.WebSocketMaxMessageSize,
        };
}
