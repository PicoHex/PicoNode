namespace PicoNode.Web.Tests;

public sealed class WebPipelineTests
{
    [Test]
    public async Task Middleware_executes_in_order()
    {
        var order = new List<int>();

        WebMiddleware first = async (ctx, next, ct) =>
        {
            order.Add(1);
            var response = await next(ctx, ct);
            order.Add(3);
            return response;
        };

        WebMiddleware second = async (ctx, next, ct) =>
        {
            order.Add(2);
            return await next(ctx, ct);
        };

        var router = new WebRouter(

            [
                WebRoute.MapGet(
                    "/",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var pipeline = BuildPipeline([first, second], router);
        var context = CreateContext("GET", "/");
        var response = await pipeline(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(order).IsEquivalentTo([1, 2, 3]);
    }

    [Test]
    public async Task Middleware_can_short_circuit()
    {
        var handlerCalled = false;

        WebMiddleware auth = (_, _, _) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 401, ReasonPhrase = "Unauthorized" }
            );

        var router = new WebRouter(

            [
                WebRoute.MapGet(
                    "/",
                    (_, _) =>
                    {
                        handlerCalled = true;
                        return ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" });
                    }
                ),
            ]
        );

        var pipeline = BuildPipeline([auth], router);
        var context = CreateContext("GET", "/");
        var response = await pipeline(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(401);
        await Assert.That(handlerCalled).IsFalse();
    }

    [Test]
    public async Task Middleware_can_modify_response()
    {
        WebMiddleware addHeader = async (ctx, next, ct) =>
        {
            var response = await next(ctx, ct);
            var headers = new HttpHeaderCollection(response.Headers);
            headers.Add("X-Custom", "value");
            return new HttpResponse
            {
                StatusCode = response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = headers,
                Body = response.Body,
            };
        };

        var router = new WebRouter(

            [
                WebRoute.MapGet(
                    "/",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var pipeline = BuildPipeline([addHeader], router);
        var context = CreateContext("GET", "/");
        var response = await pipeline(context, CancellationToken.None);

        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("X-Custom", "value"));
    }

    [Test]
    public async Task Empty_middleware_chain_routes_directly()
    {
        var router = new WebRouter(

            [
                WebRoute.MapGet(
                    "/hello",
                    static (_, _) =>
                        ValueTask.FromResult(WebResults.Text(200, "hi", "OK"))
                ),
            ]
        );

        var pipeline = BuildPipeline([], router);
        var context = CreateContext("GET", "/hello");
        var response = await pipeline(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    private static WebRequestHandler BuildPipeline(
        IReadOnlyList<WebMiddleware> middlewares,
        WebRouter router
    )
    {
        WebRequestHandler terminal = router.HandleAsync;

        for (var i = middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = middlewares[i];
            var next = terminal;
            terminal = (context, ct) => middleware(context, next, ct);
        }

        return terminal;
    }

    private static WebContext CreateContext(string method, string target) =>
        WebContext.Create(new HttpRequest { Method = method, Target = target });
}
