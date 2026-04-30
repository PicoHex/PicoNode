namespace PicoNode.Http;

public sealed class HttpRouter
{
    private readonly RouteTable<HttpRequestHandler> _routes;

    public HttpRouter(HttpRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Routes);

        var routeList = new List<(string Method, string Path, HttpRequestHandler Handler)>(
            options.Routes.Count
        );
        foreach (var route in options.Routes)
        {
            ArgumentNullException.ThrowIfNull(route);
            routeList.Add((route.Method, route.Path, route.Handler));
        }

        _routes = new RouteTable<HttpRequestHandler>(
            routeList,
            options.FallbackHandler,
            nameof(options)
        );
    }

    public ValueTask<HttpResponse> HandleAsync(
        HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.Path.AsSpan();

        if (_routes.TryMatch(path, request.Method, out var handler, out var allowHeader))
        {
            return handler(request, cancellationToken);
        }

        if (allowHeader is not null)
        {
            return ValueTask.FromResult(
                RouteTable<HttpRequestHandler>.MethodNotAllowedResponse(allowHeader)
            );
        }

        if (_routes.Fallback is not null)
        {
            return _routes.Fallback(request, cancellationToken);
        }

        return ValueTask.FromResult(RouteTable<HttpRequestHandler>.NotFoundResponse);
    }
}
