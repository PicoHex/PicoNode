namespace PicoNode.Web.Tests;

public sealed class CorsTests
{
    private static HttpRequest CreateRequest(
        string method,
        params KeyValuePair<string, string>[] headers
    )
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
            dict.TryAdd(h.Key, h.Value);

        return new HttpRequest
        {
            Method = method,
            Target = "/api/data",
            Path = "/api/data",
            Version = HttpVersion.Http11,
            HeaderFields = headers.ToList(),
            Headers = dict,
            Body = ReadOnlyMemory<byte>.Empty,
        };
    }

    private static CorsOptions DefaultOptions =>
        new() { AllowedOrigins =  ["https://example.com"] };

    [Test]
    public async Task HandlePreflight_returns_204_for_valid_options_request()
    {
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, DefaultOptions);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(204);
    }

    [Test]
    public async Task HandlePreflight_includes_allow_origin_header()
    {
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, DefaultOptions);

        var header = response!.Headers.FirstOrDefault(h => h.Key == "Access-Control-Allow-Origin");
        await Assert.That(header.Value).IsEqualTo("https://example.com");
    }

    [Test]
    public async Task HandlePreflight_returns_null_for_non_options_request()
    {
        var request = CreateRequest(
            "GET",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, DefaultOptions);

        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task HandlePreflight_returns_403_for_disallowed_origin()
    {
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://evil.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, DefaultOptions);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(403);
    }

    [Test]
    public async Task HandlePreflight_returns_null_without_origin_header()
    {
        var request = CreateRequest(
            "OPTIONS",
            new KeyValuePair<string, string>("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, DefaultOptions);

        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task HandlePreflight_includes_credentials_when_enabled()
    {
        var options = new CorsOptions
        {
            AllowedOrigins =  ["https://example.com"],
            AllowCredentials = true,
        };
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, options);

        var header = response!
            .Headers
            .FirstOrDefault(h => h.Key == "Access-Control-Allow-Credentials");
        await Assert.That(header.Value).IsEqualTo("true");
    }

    [Test]
    public async Task HandlePreflight_includes_max_age_when_set()
    {
        var options = new CorsOptions { AllowedOrigins =  ["https://example.com"], MaxAge = 3600, };
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, options);

        var header = response!.Headers.FirstOrDefault(h => h.Key == "Access-Control-Max-Age");
        await Assert.That(header.Value).IsEqualTo("3600");
    }

    [Test]
    public async Task GetResponseHeaders_adds_allow_origin_for_matching_origin()
    {
        var request = CreateRequest(
            "GET",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var headers = CorsHandler.GetResponseHeaders(request, DefaultOptions);

        var header = headers.FirstOrDefault(h => h.Key == "Access-Control-Allow-Origin");
        await Assert.That(header.Value).IsEqualTo("https://example.com");
    }

    [Test]
    public async Task GetResponseHeaders_returns_empty_for_no_origin()
    {
        var request = CreateRequest("GET", new KeyValuePair<string, string>("Host", "localhost"));

        var headers = CorsHandler.GetResponseHeaders(request, DefaultOptions);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetResponseHeaders_returns_empty_for_disallowed_origin()
    {
        var request = CreateRequest(
            "GET",
            new("Origin", "https://evil.com"),
            new("Host", "localhost")
        );

        var headers = CorsHandler.GetResponseHeaders(request, DefaultOptions);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Wildcard_origin_allows_any_origin()
    {
        var options = new CorsOptions { AllowedOrigins =  ["*"] };
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://any.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, options);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(204);
    }

    [Test]
    public async Task GetResponseHeaders_includes_exposed_headers()
    {
        var options = new CorsOptions
        {
            AllowedOrigins =  ["https://example.com"],
            ExposedHeaders =  ["X-Custom", "X-Request-Id"],
        };
        var request = CreateRequest(
            "GET",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var headers = CorsHandler.GetResponseHeaders(request, options);

        var header = headers.FirstOrDefault(h => h.Key == "Access-Control-Expose-Headers");
        await Assert.That(header.Value).IsEqualTo("X-Custom, X-Request-Id");
    }

    [Test]
    public async Task HandlePreflight_returns_403_for_disallowed_request_method()
    {
        var options = new CorsOptions
        {
            AllowedOrigins =  ["https://example.com"],
            AllowedMethods =  ["GET", "POST"],
        };
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Access-Control-Request-Method", "DELETE"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, options);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(403);
    }

    [Test]
    public async Task HandlePreflight_allows_matching_request_method()
    {
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Access-Control-Request-Method", "POST"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, DefaultOptions);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(204);
    }
}
