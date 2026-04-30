namespace PicoNode.Web.Tests;

public sealed class WebRouterTests
{
    [Test]
    public async Task HandleAsync_dispatches_exact_route()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/hello",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var context = CreateContext("GET", "/hello");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task HandleAsync_returns_404_for_unknown_path()
    {
        var router = CreateRouter([]);

        var context = CreateContext("GET", "/missing");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task HandleAsync_returns_405_when_method_does_not_match()
    {
        var router = CreateRouter(

            [
                WebRoute.MapPost(
                    "/echo",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var context = CreateContext("GET", "/echo");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(405);
        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Allow", "POST"));
    }

    [Test]
    public async Task HandleAsync_matches_parameterized_route()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/users/{id}",
                    static (ctx, _) =>
                        ValueTask.FromResult(
                            WebResults.Text(200, ctx.RouteValues["id"], "OK")
                        )
                ),
            ]
        );

        var context = CreateContext("GET", "/users/42");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(context.RouteValues["id"]).IsEqualTo("42");
    }

    [Test]
    public async Task HandleAsync_matches_multi_segment_parameterized_route()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/users/{userId}/orders/{orderId}",
                    static (ctx, _) =>
                        ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var context = CreateContext("GET", "/users/7/orders/99");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(context.RouteValues["userId"]).IsEqualTo("7");
        await Assert.That(context.RouteValues["orderId"]).IsEqualTo("99");
    }

    [Test]
    public async Task HandleAsync_prefers_exact_route_over_parameterized()
    {
        var exactCalled = false;
        var paramCalled = false;

        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/users/me",
                    (_, _) =>
                    {
                        exactCalled = true;
                        return ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" });
                    }
                ),
                WebRoute.MapGet(
                    "/users/{id}",
                    (_, _) =>
                    {
                        paramCalled = true;
                        return ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" });
                    }
                ),
            ]
        );

        var context = CreateContext("GET", "/users/me");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(exactCalled).IsTrue();
        await Assert.That(paramCalled).IsFalse();
    }

    [Test]
    public async Task HandleAsync_returns_405_for_parameterized_route_method_mismatch()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/users/{id}",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var context = CreateContext("DELETE", "/users/42");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(405);
        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Allow", "GET"));
    }

    [Test]
    public async Task HandleAsync_parameterized_route_does_not_match_extra_segments()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/users/{id}",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var context = CreateContext("GET", "/users/42/orders");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task HandleAsync_parameterized_route_does_not_match_fewer_segments()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/users/{id}",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var context = CreateContext("GET", "/users");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task HandleAsync_matches_root_path()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var context = CreateContext("GET", "/");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task HandleAsync_strips_query_from_matching()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/search",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
            ]
        );

        var context = CreateContext("GET", "/search?q=test");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task HandleAsync_sorts_allow_header_methods()
    {
        var router = CreateRouter(

            [
                WebRoute.MapPost(
                    "/echo",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
                WebRoute.Map(
                    "DELETE",
                    "/echo",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" })
                ),
            ]
        );

        var context = CreateContext("GET", "/echo");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Allow", "DELETE, POST"));
    }

    [Test]
    public async Task HandleAsync_falls_through_to_param_route_when_exact_method_mismatches()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/users/me",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                ),
                WebRoute.MapPost(
                    "/users/{id}",
                    static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 201, ReasonPhrase = "Created" })
                ),
            ]
        );

        var context = CreateContext("POST", "/users/me");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(201);
        await Assert.That(context.RouteValues["id"]).IsEqualTo("me");
    }

    [Test]
    public async Task Ctor_rejects_duplicate_exact_routes()
    {
        await Assert
            .That(
                () =>
                    CreateRouter(

                        [
                            WebRoute.MapGet(
                        "/hello",
                        static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                    ),
                            WebRoute.MapGet(
                        "/hello",
                        static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" })
                    ),
                        ]
                    )
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Ctor_rejects_duplicate_parameterized_routes()
    {
        await Assert
            .That(
                () =>
                    CreateRouter(

                        [
                            WebRoute.MapGet(
                        "/users/{id}",
                        static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                    ),
                            WebRoute.MapGet(
                        "/users/{id}",
                        static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" })
                    ),
                        ]
                    )
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Ctor_rejects_patterns_without_leading_slash()
    {
        await Assert
            .That(
                () =>
                    CreateRouter(

                        [
                            WebRoute.MapGet(
                        "hello",
                        static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                    ),
                        ]
                    )
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Ctor_rejects_patterns_with_query_component()
    {
        await Assert
            .That(
                () =>
                    CreateRouter(

                        [
                            WebRoute.MapGet(
                        "/hello?q=1",
                        static (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
                    ),
                        ]
                    )
            )
            .Throws<ArgumentException>();
    }

    private static WebRouter CreateRouter(IReadOnlyList<WebRoute> routes) => new(routes);

    private static WebRouter CreateRouterWithFallback(
        IReadOnlyList<WebRoute> routes,
        WebRequestHandler fallback
    ) => new(routes, fallback);

    private static WebContext CreateContext(string method, string target)
    {
        var q = target.IndexOf('?');
        return WebContext.Create(new HttpRequest { Method = method, Target = target, Path = q >= 0 ? target[..q] : target, QueryString = q >= 0 ? target[(q + 1)..] : string.Empty });
    }

    [Test]
    public async Task HandleAsync_invokes_fallback_for_unknown_path()
    {
        var router = CreateRouterWithFallback(
            [],
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 418, ReasonPhrase = "Teapot" })
        );

        var context = CreateContext("GET", "/missing");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(418);
    }

    [Test]
    public async Task HandleAsync_prefers_405_over_fallback()
    {
        var fallbackCalled = false;

        var router = CreateRouterWithFallback(

            [
                WebRoute.MapPost(
                    "/echo",
                    static (_, _) =>
                        ValueTask.FromResult(
                            new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                        )
                ),
            ],
            (_, _) =>
            {
                fallbackCalled = true;
                return ValueTask.FromResult(
                    new HttpResponse { StatusCode = 418, ReasonPhrase = "Teapot" }
                );
            }
        );

        var context = CreateContext("GET", "/echo");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(405);
        await Assert.That(fallbackCalled).IsFalse();
    }

    [Test]
    public async Task HandleAsync_decodes_percent_encoded_route_values()
    {
        var router = CreateRouter(

            [
                WebRoute.MapGet(
                    "/files/{name}",
                    static (ctx, _) =>
                        ValueTask.FromResult(
                            WebResults.Text(200, ctx.RouteValues["name"], "OK")
                        )
                ),
            ]
        );

        var context = CreateContext("GET", "/files/hello%20world.txt");
        var response = await router.HandleAsync(context, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(context.RouteValues["name"]).IsEqualTo("hello world.txt");
    }
}
