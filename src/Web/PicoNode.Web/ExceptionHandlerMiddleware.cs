namespace PicoNode.Web;

public sealed class ExceptionHandlerMiddleware
{
    private readonly ExceptionHandlerOptions _options;

    public ExceptionHandlerMiddleware(ExceptionHandlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<HttpResponse> InvokeAsync(
        WebContext context,
        WebRequestHandler next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _options.Logger?.Log(
                LogLevel.Error,
                new EventId(0),
                $"Unhandled exception: {context.Request.Method} {context.Request.Target}",
                null,
                ex);

            try
            {
                var response = _options.ExceptionHandler(context, ex);
                return response ?? new HttpResponse
                {
                    StatusCode = 500,
                    ReasonPhrase = "Internal Server Error",
                };
            }
            catch (Exception handlerEx)
            {
                _options.Logger?.Log(
                    LogLevel.Critical,
                    new EventId(0),
                    "Exception handler itself threw an exception",
                    null,
                    handlerEx);

                return new HttpResponse
                {
                    StatusCode = 500,
                    ReasonPhrase = "Internal Server Error",
                };
            }
        }
    }

    public static ExceptionHandlerMiddleware CreateDefault(ILogger? logger = null)
    {
        return new ExceptionHandlerMiddleware(new ExceptionHandlerOptions
        {
            Logger = logger,
            ExceptionHandler = static (ctx, ex) =>
            {
                var traceId = Guid.NewGuid().ToString("N")[..8];
                var json = $$"""{"error":"Internal Server Error","traceId":"{{traceId}}"}""";
                return WebResults.Json(500, json, "Internal Server Error");
            },
        });
    }
}
