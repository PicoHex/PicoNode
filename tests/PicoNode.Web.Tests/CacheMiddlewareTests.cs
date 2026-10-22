namespace PicoNode.Web.Tests;

public sealed class CacheMiddlewareTests
{
    /// <summary>The guard's default identity headers, plus the encoding dimension.</summary>
    private static readonly string[] VaryDimensions =
    [
        "Accept-Encoding",
        "Authorization",
        "Cookie",
        "X-Session-Id",
        "X-Api-Key",
    ];

    [Test]
    public async Task Returns_304_when_ETag_matches_IfNoneMatch()
    {
        var middleware = new CacheMiddleware();
        var path = "/test-" + Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes("cached content");
        var context = CreateContext("GET", path, ifNoneMatch: null);

        // First call: cache the response
        var response1 = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = body,
                    }
                ),
            CancellationToken.None
        );

        await Assert.That(response1.StatusCode).IsEqualTo(200);
        var etag = GetHeader(response1, "ETag");
        await Assert.That(etag).IsNotNull();

        // Second call with matching ETag
        var context2 = CreateContext("GET", path, ifNoneMatch: etag);
        var response2 = await middleware.InvokeAsync(
            context2,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = body,
                    }
                ),
            CancellationToken.None
        );

        await Assert.That(response2.StatusCode).IsEqualTo(304);
    }

    [Test]
    public async Task Passes_through_when_ETag_does_not_match()
    {
        var middleware = new CacheMiddleware();
        var body = Encoding.UTF8.GetBytes("content");
        var context = CreateContext("GET", "/other", ifNoneMatch: "\"wrong-etag\"");

        var response = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = body,
                    }
                ),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Skips_cache_for_non_GET_requests()
    {
        var middleware = new CacheMiddleware();
        var context = CreateContext("POST", "/data", ifNoneMatch: null);

        var response = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 201, ReasonPhrase = "Created" }
                ),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(201);
        await Assert.That(GetHeader(response, "ETag")).IsNull();
    }

    [Test]
    public async Task Adds_CacheControl_header()
    {
        var middleware = new CacheMiddleware();
        var context = CreateContext("GET", "/", ifNoneMatch: null);

        var response = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = "data"u8.ToArray(),
                    }
                ),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "Cache-Control")).IsEqualTo("private, max-age=3600");
    }

    [Test]
    public async Task Evicts_oldest_entry_when_cache_exceeds_capacity()
    {
        // Arrange: cache capacity = 3
        var middleware = new CacheMiddleware(TimeSpan.FromHours(1), maxCacheSize: 3);
        var body = "x"u8.ToArray();
        var etag = CacheMiddleware.GenerateETag(body);

        // Fill cache with 3 entries
        await middleware.InvokeAsync(
            CreateContext("GET", "/a", null),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body }),
            CancellationToken.None
        );
        await middleware.InvokeAsync(
            CreateContext("GET", "/b", null),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body }),
            CancellationToken.None
        );
        await middleware.InvokeAsync(
            CreateContext("GET", "/c", null),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body }),
            CancellationToken.None
        );

        // Act: add a 4th entry — triggers eviction of oldest
        await middleware.InvokeAsync(
            CreateContext("GET", "/d", null),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body }),
            CancellationToken.None
        );

        // Assert:
        // "/b" and "/c" should still be cached → 304
        var responseB = await middleware.InvokeAsync(
            CreateContext("GET", "/b", etag),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body }),
            CancellationToken.None
        );
        await Assert.That(responseB.StatusCode).IsEqualTo(304);

        var responseC = await middleware.InvokeAsync(
            CreateContext("GET", "/c", etag),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body }),
            CancellationToken.None
        );
        await Assert.That(responseC.StatusCode).IsEqualTo(304);

        // "/d" should also be cached (newest) → 304
        var responseD = await middleware.InvokeAsync(
            CreateContext("GET", "/d", etag),
            (_, _) => ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body }),
            CancellationToken.None
        );
        await Assert.That(responseD.StatusCode).IsEqualTo(304);

        // "/a" (oldest) should be evicted → 200 (handler called)
        var handlerCalled = false;
        var responseA = await middleware.InvokeAsync(
            CreateContext("GET", "/a", etag),
            (_, _) =>
            {
                handlerCalled = true;
                return ValueTask.FromResult(new HttpResponse { StatusCode = 200, Body = body });
            },
            CancellationToken.None
        );
        await Assert.That(responseA.StatusCode).IsEqualTo(200);
        await Assert.That(handlerCalled).IsTrue();
    }

    // ---- T3.2: identity headers are private, and the body is never copied ----------------

    [Test]
    [Arguments("Authorization", "Bearer token")]
    [Arguments("Cookie", "sid=abc")]
    [Arguments("X-Session-Id", "s-1")]
    [Arguments("X-Api-Key", "k-1")]
    public async Task Header_authenticated_response_is_private(string headerName, string value)
    {
        var middleware = new CacheMiddleware();
        var path = "/header-auth-" + Guid.NewGuid();
        var handlerCalls = 0;

        ValueTask<HttpResponse> Handler(WebContext _, CancellationToken __)
        {
            handlerCalls++;
            return ValueTask.FromResult(
                new HttpResponse
                {
                    StatusCode = 200,
                    ReasonPhrase = "OK",
                    Body = "secret"u8.ToArray(),
                }
            );
        }

        var response = await middleware.InvokeAsync(
            CreateContextWithHeaders("GET", path, (headerName, value)),
            Handler,
            CancellationToken.None
        );

        var cacheControl = GetHeader(response, "Cache-Control");
        var privateOrAbsent =
            cacheControl is null
            || cacheControl.StartsWith("private", StringComparison.Ordinal);
        await Assert
            .That(privateOrAbsent)
            .IsTrue()
            .Because($"{headerName} identifies a user: never advertise public caching");
        await Assert
            .That(GetHeader(response, "ETag"))
            .IsNull()
            .Because("identity-carrying responses must not enter the path-keyed cache");

        // Same request twice: had the first one been cached, the second would be a 304.
        var second = await middleware.InvokeAsync(
            CreateContextWithHeaders("GET", path, (headerName, value)),
            Handler,
            CancellationToken.None
        );
        await Assert.That(second.StatusCode).IsEqualTo(200);
        await Assert
            .That(handlerCalls)
            .IsEqualTo(2)
            .Because("nothing was stored, so the origin ran both times");
    }

    [Test]
    public async Task Identity_headers_are_varied()
    {
        var middleware = new CacheMiddleware();
        var response = await InvokeGet(
            middleware,
            "/vary-" + Guid.NewGuid(),
            queryString: null,
            body: "anonymous content"
        );

        var vary = GetHeader(response, "Vary");
        await Assert.That(vary).IsNotNull();
        foreach (var name in VaryDimensions)
        {
            await Assert
                .That(vary!)
                .Contains(name)
                .Because("a shared cache must not serve anonymous bytes to an identified request");
        }
    }

    [Test]
    public async Task Cacheable_get_is_not_copied_or_hashed_twice()
    {
        var middleware = new CacheMiddleware();
        var body = new byte[40 * 1024];
        Random.Shared.NextBytes(body);
        var path = "/alloc-" + Guid.NewGuid();

        ValueTask<HttpResponse> Handler(WebContext _, CancellationToken __) =>
            ValueTask.FromResult(
                new HttpResponse { StatusCode = 200, ReasonPhrase = "OK", Body = body }
            );

        // Warm up JIT, dictionary/queue segments, the path string.
        for (var i = 0; i < 5; i++)
        {
            await middleware.InvokeAsync(
                CreateContext("GET", path, ifNoneMatch: null),
                Handler,
                CancellationToken.None
            );
        }

        var context = CreateContext("GET", path, ifNoneMatch: null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var response = await middleware.InvokeAsync(context, Handler, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert
            .That(allocated)
            .IsLessThan(1024)
            .Because($"no 40 KB copy may land in the cache (allocated {allocated} bytes)");
    }

    [Test]
    public async Task Configured_identity_header_is_guarded_and_varied()
    {
        var middleware = new CacheMiddleware { IdentityHeaders = ["X-Tenant-Id"] };
        var response = await middleware.InvokeAsync(
            CreateContextWithHeaders("GET", "/tenant-" + Guid.NewGuid(), ("X-Tenant-Id", "t-1")),
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "x"u8.ToArray() }
                ),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "ETag")).IsNull();
        await Assert
            .That(GetHeader(response, "Cache-Control"))
            .IsNull()
            .Because("a configured identity header is treated exactly like the defaults");

        var anonymous = await InvokeGet(
            middleware,
            "/tenant-anon-" + Guid.NewGuid(),
            queryString: null,
            body: "x"
        );
        await Assert.That(GetHeader(anonymous, "Vary")).Contains("X-Tenant-Id");
    }

    [Test]
    public async Task Public_is_only_advertised_when_the_host_opts_in()
    {
        var privateDefault = await InvokeGet(
            new CacheMiddleware(),
            "/optin-priv-" + Guid.NewGuid(),
            queryString: null,
            body: "x"
        );
        await Assert
            .That(GetHeader(privateDefault, "Cache-Control"))
            .StartsWith("private")
            .Because("D1: the default is private");

        var optedIn = await InvokeGet(
            new CacheMiddleware
            {
                ShouldCache = ctx => ctx.Path.StartsWith("/optin-pub", StringComparison.Ordinal),
            },
            "/optin-pub-" + Guid.NewGuid(),
            queryString: null,
            body: "x"
        );
        await Assert
            .That(GetHeader(optedIn, "Cache-Control"))
            .StartsWith("public")
            .Because("a host that opts a path into shared caching must still get public");

        var declined = await InvokeGet(
            new CacheMiddleware { ShouldCache = _ => false },
            "/optin-none-" + Guid.NewGuid(),
            queryString: null,
            body: "x"
        );
        await Assert
            .That(GetHeader(declined, "Cache-Control"))
            .StartsWith("private")
            .Because("an opt-in predicate that declines falls back to private");

        // The opt-in cannot outvote the identity guard.
        var identifiedContext = CreateContextWithHeaders(
            "GET",
            "/optin-auth-" + Guid.NewGuid(),
            ("X-Api-Key", "k")
        );
        var optIn = new CacheMiddleware { ShouldCache = _ => true };
        var optedInButIdentified = await optIn.InvokeAsync(
            identifiedContext,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse { StatusCode = 200, Body = "x"u8.ToArray() }
                ),
            CancellationToken.None
        );
        await Assert
            .That(GetHeader(optedInButIdentified, "Cache-Control"))
            .IsNull()
            .Because("ShouldCache must never advertise public caching for an identified request");
    }

    private static WebContext CreateContext(string method, string path, string? ifNoneMatch)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (ifNoneMatch is not null)
        {
            headers.Add(new KeyValuePair<string, string>("If-None-Match", ifNoneMatch));
        }

        return WebContext.Create(
            new HttpRequest
            {
                Method = method,
                Target = path,
                Path = path,
                HeaderFields = headers,
                Headers = headers.ToDictionary(
                    h => h.Key,
                    h => h.Value,
                    StringComparer.OrdinalIgnoreCase
                ),
            }
        );
    }

    [Test]
    public async Task Existing_vary_header_is_extended_not_replaced()
    {
        var middleware = new CacheMiddleware();
        var body = "z"u8.ToArray();

        // The app (or a middleware already inside the pipeline) declared its own Vary.
        var response = await middleware.InvokeAsync(
            CreateContext("GET", "/vary-merge-" + Guid.NewGuid(), ifNoneMatch: null),
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = body,
                        Headers =
                        [
                            new KeyValuePair<string, string>("Vary", "Accept-Encoding"),
                        ],
                    }
                ),
            CancellationToken.None
        );

        var varyFields = GetHeaders(response, "Vary");
        await Assert
            .That(varyFields.Count)
            .IsEqualTo(2)
            .Because("the app's Vary must be extended by a second field line, not replaced");
        foreach (var name in VaryDimensions)
        {
            await Assert.That(string.Join(", ", varyFields)).Contains(name);
        }

        // A response that already declares every identity dimension is left alone.
        var alreadyVaried = await middleware.InvokeAsync(
            CreateContext("GET", "/vary-merge-done-" + Guid.NewGuid(), ifNoneMatch: null),
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = body,
                        Headers =
                        [
                            new KeyValuePair<string, string>(
                                "Vary",
                                "Accept-Encoding, Authorization, Cookie, X-Session-Id, X-Api-Key"
                            ),
                        ],
                    }
                ),
            CancellationToken.None
        );

        await Assert
            .That(GetHeaders(alreadyVaried, "Vary").Count)
            .IsEqualTo(1)
            .Because("no duplicate Vary field is added when those dimensions are already varied");
    }

    private static List<string> GetHeaders(HttpResponse response, string name)
    {
        var values = new List<string>();
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                values.Add(header.Value);
        }
        return values;
    }

    private static WebContext CreateContextWithHeaders(
        string method,
        string path,
        params (string Name, string Value)[] headers
    )
    {
        var headerFields = headers
            .Select(h => new KeyValuePair<string, string>(h.Name, h.Value))
            .ToList();

        return WebContext.Create(
            new HttpRequest
            {
                Method = method,
                Target = path,
                Path = path,
                HeaderFields = headerFields,
                Headers = headerFields.ToDictionary(
                    h => h.Key,
                    h => h.Value,
                    StringComparer.OrdinalIgnoreCase
                ),
            }
        );
    }

    private static string? GetHeader(HttpResponse response, string name)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }
        return null;
    }

    private static WebContext CreateQueryContext(
        string path,
        string? queryString,
        string? authHeader,
        string? ifNoneMatch = null
    )
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (authHeader is not null)
        {
            headers.Add(new KeyValuePair<string, string>("Authorization", authHeader));
        }
        if (ifNoneMatch is not null)
        {
            headers.Add(new KeyValuePair<string, string>("If-None-Match", ifNoneMatch));
        }

        return WebContext.Create(
            new HttpRequest
            {
                Method = "GET",
                Target = path + (queryString ?? string.Empty),
                Path = path,
                QueryString = queryString ?? string.Empty,
                HeaderFields = headers,
                Headers = headers.ToDictionary(
                    h => h.Key,
                    h => h.Value,
                    StringComparer.OrdinalIgnoreCase
                ),
            }
        );
    }

    private static async Task<HttpResponse> InvokeGet(
        CacheMiddleware middleware,
        string path,
        string? queryString,
        string body,
        string? authHeader = null,
        string? ifNoneMatch = null
    )
    {
        var context = CreateQueryContext(path, queryString, authHeader, ifNoneMatch);
        return await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = Encoding.UTF8.GetBytes(body),
                    }
                ),
            CancellationToken.None
        );
    }

    [Test]
    public async Task Cache_key_includes_query_string()
    {
        var middleware = new CacheMiddleware();
        var first = await InvokeGet(middleware, "/api", "?a=1", body: "AAA");
        var etag1 = GetHeader(first, "ETag")!;
        // Same path, different query, revalidating with the first entry's ETag:
        // pre-fix the shared "/api" entry matches → 304; post-fix the query is
        // part of the key → no match → the origin is invoked → 200.
        var second = await InvokeGet(middleware, "/api", "?a=2", body: "BBB", ifNoneMatch: etag1);

        await Assert
            .That(second.StatusCode)
            .IsEqualTo(200)
            .Because("different query strings must not share a cache entry");
        await Assert
            .That(GetHeader(first, "ETag"))
            .IsNotEqualTo(GetHeader(second, "ETag"))
            .Because("different query strings must not share a cache entry");
    }

    [Test]
    public async Task Authenticated_requests_are_not_cached()
    {
        var middleware = new CacheMiddleware();
        var response = await InvokeGet(
            middleware,
            "/private",
            null,
            body: "secret",
            authHeader: "Bearer x"
        );

        await Assert
            .That(GetHeader(response, "ETag"))
            .IsNull()
            .Because("responses to authenticated requests must not enter the shared cache");
        await Assert
            .That(GetHeader(response, "Cache-Control"))
            .IsNull()
            .Because("authenticated responses must not advertise public cacheability");
    }

    [Test]
    public async Task Not_modified_response_includes_vary_header()
    {
        var middleware = new CacheMiddleware();
        var path = "/vary-test-" + Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes("cached content");
        var context = CreateContext("GET", path, ifNoneMatch: null);

        var response1 = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = body,
                    }
                ),
            CancellationToken.None
        );
        var etag = GetHeader(response1, "ETag");

        var context2 = CreateContext("GET", path, ifNoneMatch: etag);
        var response2 = await middleware.InvokeAsync(
            context2,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Body = body,
                    }
                ),
            CancellationToken.None
        );

        await Assert.That(response2.StatusCode).IsEqualTo(304);
        await Assert
            .That(GetHeader(response2, "Vary"))
            .Contains("Accept-Encoding")
            .Because("304 responses must keep the Vary header for correct cache semantics");
        await Assert
            .That(GetHeader(response2, "Vary"))
            .Contains("X-Session-Id")
            .Because("a 304 must vary on what the 200 it replaces varied on");
        await Assert
            .That(GetHeader(response2, "Cache-Control"))
            .StartsWith("private")
            .Because("a 304 must not upgrade the stored variant to shared cacheability");
    }

    [Test]
    public async Task Uncached_streaming_response_uses_configured_max_age()
    {
        var middleware = new CacheMiddleware(TimeSpan.FromSeconds(60));
        var context = CreateContext("GET", "/stream-" + Guid.NewGuid(), ifNoneMatch: null);

        var response = await middleware.InvokeAsync(
            context,
            (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        BodyStream = new MemoryStream([1, 2, 3]),
                    }
                ),
            CancellationToken.None
        );

        await Assert
            .That(GetHeader(response, "Cache-Control"))
            .IsEqualTo("private, max-age=60")
            .Because("the header must reflect the configured max age, not the default");
    }
}
