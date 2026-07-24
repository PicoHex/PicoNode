namespace PicoNode.Web;

public sealed class ExceptionHandlerOptions
{
    public required Func<WebContext, Exception, HttpResponse> ExceptionHandler { get; init; }

    public ILogger? Logger { get; init; }
}
