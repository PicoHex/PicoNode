namespace PicoNode.Web;

public delegate ValueTask<HttpResponse> WebMiddleware(
    WebContext context,
    WebRequestHandler next,
    CancellationToken cancellationToken
);
