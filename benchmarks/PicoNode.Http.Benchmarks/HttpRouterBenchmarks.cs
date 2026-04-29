namespace PicoNode.Http.Benchmarks;

[BenchmarkClass(Description = "PicoNode.Http HttpRouter exact-route dispatch")]
public sealed partial class HttpRouterBenchmarks
{
    private static readonly HttpResponse NoContentResponse =
        new() { StatusCode = 204, ReasonPhrase = "No Content", };

    private static readonly HttpResponse FallbackResponse =
        new()
        {
            StatusCode = 404,
            ReasonPhrase = "Not Found",
            Body = Encoding.ASCII.GetBytes("router-missing"),
        };

    [Params(1, 8, 32, 128)]
    public int RouteCount { get; set; }

    private HttpRouter _router = null!;
    private HttpRequest _hitRequest = null!;
    private HttpRequest _missRequest = null!;
    private HttpRequest _methodNotAllowedRequest = null!;

    [GlobalSetup]
    public void Setup()
    {
        var routes = new List<HttpRoute>(RouteCount);

        for (var index = 0; index < RouteCount; index++)
        {
            var path = $"/route-{index}";
            routes.Add(
                HttpRoute.MapGet(path, static (_, _) => ValueTask.FromResult(NoContentResponse))
            );
        }

        _router = new HttpRouter(
            new HttpRouterOptions
            {
                Routes = routes,
                FallbackHandler = static (_, _) => ValueTask.FromResult(FallbackResponse),
            }
        );

        _hitRequest = new HttpRequest { Method = "GET", Target = $"/route-{RouteCount - 1}" };
        _missRequest = new HttpRequest { Method = "GET", Target = "/missing" };
        _methodNotAllowedRequest = new HttpRequest { Method = "POST", Target = "/route-0" };

        var hitResponse = _router
            .HandleAsync(_hitRequest, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var missResponse = _router
            .HandleAsync(_missRequest, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var methodNotAllowedResponse = _router
            .HandleAsync(_methodNotAllowedRequest, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (hitResponse.StatusCode != 204)
        {
            throw new InvalidOperationException(
                $"Expected 204 route hit response during setup, but observed {hitResponse.StatusCode}."
            );
        }

        if (missResponse.StatusCode != 404)
        {
            throw new InvalidOperationException(
                $"Expected 404 fallback response during setup, but observed {missResponse.StatusCode}."
            );
        }

        if (methodNotAllowedResponse.StatusCode != 405)
        {
            throw new InvalidOperationException(
                $"Expected 405 method-not-allowed response during setup, but observed {methodNotAllowedResponse.StatusCode}."
            );
        }
    }

    [Benchmark]
    public void RouteHit()
    {
        var response = _router
            .HandleAsync(_hitRequest, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (response.StatusCode != 204)
        {
            throw new InvalidOperationException(
                $"Expected 204 response, but observed {response.StatusCode}."
            );
        }
    }

    [Benchmark]
    public void RouteMissWithFallback()
    {
        var response = _router
            .HandleAsync(_missRequest, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (response.StatusCode != 404)
        {
            throw new InvalidOperationException(
                $"Expected 404 response, but observed {response.StatusCode}."
            );
        }
    }

    [Benchmark]
    public void MethodNotAllowed()
    {
        var response = _router
            .HandleAsync(_methodNotAllowedRequest, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (response.StatusCode != 405)
        {
            throw new InvalidOperationException(
                $"Expected 405 response, but observed {response.StatusCode}."
            );
        }
    }
}
