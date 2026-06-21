namespace PicoNode.Web.Tests;

public sealed class CacheMiddlewareTests
{
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

        await Assert.That(GetHeader(response, "Cache-Control")).IsEqualTo("public, max-age=3600");
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

    private static string? GetHeader(HttpResponse response, string name)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }
        return null;
    }
}
