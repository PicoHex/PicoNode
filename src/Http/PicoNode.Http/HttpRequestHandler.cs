namespace PicoNode.Http;

public delegate ValueTask<HttpResponse> HttpRequestHandler(
    HttpRequest request,
    CancellationToken cancellationToken
);
