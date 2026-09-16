namespace PicoNode.Http.Tests;

public sealed class HttpRouterTests
{
    [Test]
    public async Task HandleAsync_dispatches_exact_method_and_path_match()
    {
        var router = CreateRouter([
            Route<HttpRequestHandler>.MapGet(
                "/hello",
                static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
            ),
        ]);

        var response = await router.HandleAsync(
            CreateRequest("GET", "/hello"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task HandleAsync_matches_path_component_of_request_target()
    {
        var router = CreateRouter([
            Route<HttpRequestHandler>.MapGet(
                "/hello",
                static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
            ),
        ]);

        var response = await router.HandleAsync(
            CreateRequest("GET", "/hello?name=pico"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task HandleAsync_treats_trailing_slashes_as_distinct_paths()
    {
        var router = CreateRouter([
            Route<HttpRequestHandler>.MapGet(
                "/hello",
                static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
            ),
        ]);

        var response = await router.HandleAsync(
            CreateRequest("GET", "/hello/"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task HandleAsync_returns_default_404_when_path_is_unknown_and_no_fallback_exists()
    {
        var router = CreateRouter([]);

        var response = await router.HandleAsync(
            CreateRequest("GET", "/missing"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(404);
        await Assert.That(response.ReasonPhrase).IsEqualTo("Not Found");
    }

    [Test]
    public async Task HandleAsync_invokes_fallback_for_unknown_path()
    {
        var router = new HttpRouter(
            new HttpRouterOptions
            {
                Routes = [],
                FallbackHandler = static (_, _) =>
                    ValueTask.FromResult(
                        new HttpResponse { StatusCode = 418, ReasonPhrase = "Teapot" }
                    ),
            }
        );

        var response = await router.HandleAsync(
            CreateRequest("GET", "/missing"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(418);
    }

    [Test]
    public async Task HandleAsync_returns_405_when_path_exists_for_other_methods()
    {
        var router = CreateRouter([
            Route<HttpRequestHandler>.MapPost(
                "/echo",
                static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
            ),
        ]);

        var response = await router.HandleAsync(
            CreateRequest("GET", "/echo"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(405);
        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Allow", "POST"));
    }

    [Test]
    public async Task HandleAsync_sorts_and_joins_allow_header_values()
    {
        var router = CreateRouter([
            Route<HttpRequestHandler>.MapPost(
                "/echo",
                static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
            ),
            Route<HttpRequestHandler>.Map(
                "DELETE",
                "/echo",
                static (_, _) =>
                    ValueTask.FromResult(
                        new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                    )
            ),
        ]);

        var response = await router.HandleAsync(
            CreateRequest("GET", "/echo"),
            CancellationToken.None
        );

        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Allow", "DELETE, POST"));
    }

    [Test]
    public async Task HandleAsync_prefers_405_over_fallback_when_path_exists_for_other_methods()
    {
        var matchedHandlerCalled = false;
        var fallbackHandlerCalled = false;

        var router = new HttpRouter(
            new HttpRouterOptions
            {
                Routes =
                [
                    new Route<HttpRequestHandler>
                    {
                        Method = "POST",
                        Path = "/echo",
                        Handler = (_, _) =>
                        {
                            matchedHandlerCalled = true;
                            return ValueTask.FromResult(
                                new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                            );
                        },
                    },
                ],
                FallbackHandler = (_, _) =>
                {
                    fallbackHandlerCalled = true;
                    return ValueTask.FromResult(
                        new HttpResponse { StatusCode = 418, ReasonPhrase = "Teapot" }
                    );
                },
            }
        );

        var response = await router.HandleAsync(
            CreateRequest("GET", "/echo"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(405);
        await Assert.That(matchedHandlerCalled).IsFalse();
        await Assert.That(fallbackHandlerCalled).IsFalse();
    }

    [Test]
    public async Task MapGet_creates_get_route()
    {
        var route = Route<HttpRequestHandler>.MapGet(
            "/hello",
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
        );

        await Assert.That(route.Method).IsEqualTo("GET");
        await Assert.That(route.Path).IsEqualTo("/hello");
    }

    [Test]
    public async Task MapPost_creates_post_route()
    {
        var route = Route<HttpRequestHandler>.MapPost(
            "/echo",
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
        );

        await Assert.That(route.Method).IsEqualTo("POST");
        await Assert.That(route.Path).IsEqualTo("/echo");
    }

    [Test]
    public async Task MapPut_creates_put_route()
    {
        var route = Route<HttpRequestHandler>.MapPut(
            "/resource",
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
        );

        await Assert.That(route.Method).IsEqualTo("PUT");
        await Assert.That(route.Path).IsEqualTo("/resource");
    }

    [Test]
    public async Task MapDelete_creates_delete_route()
    {
        var route = Route<HttpRequestHandler>.MapDelete(
            "/resource",
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                )
        );

        await Assert.That(route.Method).IsEqualTo("DELETE");
        await Assert.That(route.Path).IsEqualTo("/resource");
    }

    [Test]
    public async Task Ctor_rejects_duplicate_method_and_path_pairs()
    {
        await Assert
            .That(() =>
                CreateRouter([
                    new Route<HttpRequestHandler>
                    {
                        Method = "GET",
                        Path = "/hello",
                        Handler = static (_, _) =>
                            ValueTask.FromResult(
                                new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                            ),
                    },
                    new Route<HttpRequestHandler>
                    {
                        Method = "GET",
                        Path = "/hello",
                        Handler = static (_, _) =>
                            ValueTask.FromResult(
                                new HttpResponse { StatusCode = 204, ReasonPhrase = "No Content" }
                            ),
                    },
                ])
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Ctor_rejects_paths_without_leading_slash()
    {
        await Assert
            .That(() =>
                CreateRouter([
                    new Route<HttpRequestHandler>
                    {
                        Method = "GET",
                        Path = "hello",
                        Handler = static (_, _) =>
                            ValueTask.FromResult(
                                new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                            ),
                    },
                ])
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Ctor_rejects_paths_with_query_component()
    {
        await Assert
            .That(() =>
                CreateRouter([
                    new Route<HttpRequestHandler>
                    {
                        Method = "GET",
                        Path = "/hello?name=pico",
                        Handler = static (_, _) =>
                            ValueTask.FromResult(
                                new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                            ),
                    },
                ])
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Ctor_rejects_blank_methods()
    {
        await Assert
            .That(() =>
                CreateRouter([
                    new Route<HttpRequestHandler>
                    {
                        Method = " ",
                        Path = "/hello",
                        Handler = static (_, _) =>
                            ValueTask.FromResult(
                                new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }
                            ),
                    },
                ])
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task HandleAsync_HEAD_falls_back_to_GET_handler()
    {
        var router = CreateRouter([
            Route<HttpRequestHandler>.MapGet(
                "/hello",
                static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
            ),
        ]);

        var response = await router.HandleAsync(
            CreateRequest("HEAD", "/hello"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Allow_header_for_GET_route_includes_HEAD()
    {
        var router = CreateRouter([
            Route<HttpRequestHandler>.MapGet(
                "/hello",
                static (_, _) =>
                    ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" })
            ),
        ]);

        var response = await router.HandleAsync(
            CreateRequest("POST", "/hello"),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(405);
        await Assert
            .That(response.Headers)
            .Contains(new KeyValuePair<string, string>("Allow", "GET, HEAD"));
    }

    [Test]
    public async Task NotFound_responses_are_not_shared_instances()
    {
        var router = CreateRouter([]);

        var first = await router.HandleAsync(CreateRequest("GET", "/a"), CancellationToken.None);
        var second = await router.HandleAsync(CreateRequest("GET", "/b"), CancellationToken.None);

        await Assert.That(ReferenceEquals(first, second)).IsFalse();

        // Middleware adds headers to the returned response; a shared instance
        // would leak them into every other 404 (and mutate concurrently).
        first.Headers.Add("X-Test", "1");
        await Assert.That(second.Headers.TryGetValue("X-Test", out _)).IsFalse();
    }

    private static HttpRouter CreateRouter(IReadOnlyList<Route<HttpRequestHandler>> routes) =>
        new(new HttpRouterOptions { Routes = routes });

    private static HttpRequest CreateRequest(string method, string target)
    {
        var queryIndex = target.IndexOf('?');
        return new()
        {
            Method = method,
            Target = target,
            Path = queryIndex >= 0 ? target[..queryIndex] : target,
            QueryString = queryIndex >= 0 ? target[(queryIndex + 1)..] : string.Empty,
        };
    }
}
