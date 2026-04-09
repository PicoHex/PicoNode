namespace PicoNode.Web;

public delegate ValueTask<HttpResponse> WebRequestHandler(
    WebContext context,
    CancellationToken cancellationToken
);
