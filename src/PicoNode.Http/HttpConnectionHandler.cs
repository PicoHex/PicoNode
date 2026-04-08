using PicoNode.Http.Internal;

namespace PicoNode.Http;

public sealed class HttpConnectionHandler : ITcpConnectionHandler
{
    private static readonly HttpResponse InternalServerErrorResponse = new()
    {
        StatusCode = 500,
        ReasonPhrase = "Internal Server Error",
    };

    private readonly HttpConnectionHandlerOptions _options;
    private readonly HttpRequestHandler _requestHandler;

    public HttpConnectionHandler(HttpConnectionHandlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxRequestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxRequestBytes, "MaxRequestBytes must be greater than zero.");
        }

        _options = options;
        _requestHandler = options.RequestHandler
            ?? throw new ArgumentException("A request handler is required.", nameof(options));
    }

    public Task OnConnectedAsync(ITcpConnectionContext connection, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask<SequencePosition> OnReceivedAsync(
        ITcpConnectionContext connection,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var parseResult = HttpRequestParser.Parse(buffer, _options);

        return parseResult switch
        {
            { Status: HttpRequestParseStatus.Incomplete } => ValueTask.FromResult(parseResult.Consumed),
            { Status: HttpRequestParseStatus.Success, Request: { } request } => HandleRequestAsync(
                connection,
                request,
                parseResult.Consumed,
                cancellationToken
            ),
            { Status: HttpRequestParseStatus.Rejected, Error: { } error } => HandleProtocolErrorAsync(
                connection,
                parseResult.Consumed,
                error,
                cancellationToken
            ),
            _ => throw new InvalidOperationException("Unexpected HTTP parse status."),
        };
    }

    public Task OnClosedAsync(
        ITcpConnectionContext connection,
        TcpCloseReason reason,
        Exception? error,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    private async ValueTask<SequencePosition> HandleRequestAsync(
        ITcpConnectionContext connection,
        HttpRequest request,
        SequencePosition consumed,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await _requestHandler(request, cancellationToken);
            var shouldClose = ShouldCloseConnection(request);

            await SendResponseAsync(connection, response, shouldClose, cancellationToken);
            return consumed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await SendResponseAsync(
                connection,
                InternalServerErrorResponse,
                closeConnection: true,
                cancellationToken
            );

            return consumed;
        }
    }

    private async ValueTask<SequencePosition> HandleProtocolErrorAsync(
        ITcpConnectionContext connection,
        SequencePosition consumed,
        HttpRequestParseError error,
        CancellationToken cancellationToken
    )
    {
        var (statusCode, reasonPhrase) = error switch
        {
            HttpRequestParseError.RequestTooLarge => (413, "Payload Too Large"),
            HttpRequestParseError.UnsupportedFraming => (501, "Not Implemented"),
            HttpRequestParseError.InvalidRequestLine
            or HttpRequestParseError.InvalidHeader
            or HttpRequestParseError.InvalidHostHeader
            or HttpRequestParseError.MissingHostHeader
            or HttpRequestParseError.DuplicateContentLength
            or HttpRequestParseError.InvalidContentLength => (400, "Bad Request"),
            _ => (400, "Bad Request"),
        };

        var response = new HttpResponse
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
        };

        await SendResponseAsync(connection, response, closeConnection: true, cancellationToken);
        return consumed;
    }

    private async ValueTask SendResponseAsync(
        ITcpConnectionContext connection,
        HttpResponse response,
        bool closeConnection,
        CancellationToken cancellationToken
    )
    {
        var payload = HttpResponseSerializer.Serialize(
            response,
            closeConnection,
            _options.ServerHeader
        );

        try
        {
            await connection.SendAsync(payload, cancellationToken);
        }
        finally
        {
            if (closeConnection)
            {
                connection.Close();
            }
        }
    }

    private static bool ShouldCloseConnection(HttpRequest request)
    {
        foreach (var header in request.HeaderFields)
        {
            if (!header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in header.Value.Split(','))
            {
                if (string.Equals(token.Trim(), "close", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
