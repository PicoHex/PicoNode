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

    private static CorsOptions DefaultOptions => new() { AllowedOrigins = ["https://example.com"] };

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
            AllowedOrigins = ["https://example.com"],
            AllowCredentials = true,
        };
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, options);

        var header = response!.Headers.FirstOrDefault(h =>
            h.Key == "Access-Control-Allow-Credentials"
        );
        await Assert.That(header.Value).IsEqualTo("true");
    }

    [Test]
    public async Task Wildcard_with_credentials_throws_on_first_use()
    {
        // `Access-Control-Allow-Origin: *` with credentials is invalid per the Fetch
        // spec. Reflecting every origin instead (the old behaviour) silently turned
        // the wildcard into universal, credentialed trust — fail loud instead.
        var options = new CorsOptions { AllowedOrigins = ["*"], AllowCredentials = true };
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://evil.example"),
            new("Host", "localhost")
        );

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CorsHandler.HandlePreflight(request, options)
        );

        await Assert.That(ex.Message).Contains("credentials");
    }

    [Test]
    public async Task GetResponseHeaders_wildcard_with_credentials_throws()
    {
        var options = new CorsOptions { AllowedOrigins = ["*"], AllowCredentials = true };
        var request = CreateRequest(
            "GET",
            new("Origin", "https://app.example"),
            new("Host", "localhost")
        );

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CorsHandler.GetResponseHeaders(request, options)
        );

        await Assert.That(ex.Message).Contains("credentials");
    }

    [Test]
    public async Task Empty_allowed_origins_throws_on_first_use()
    {
        // An empty allow-list used to answer every cross-origin request with 403 and
        // no diagnostic; that is a misconfiguration, not a policy.
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var ex = Assert.Throws<InvalidOperationException>(() =>
            CorsHandler.HandlePreflight(request, new CorsOptions())
        );

        await Assert.That(ex.Message).Contains("AllowedOrigins");
    }

    [Test]
    public async Task HandlePreflight_rejects_null_options()
    {
        // Guard clause: a null policy is a programming error, not a CORS decision.
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var ex = Assert.Throws<ArgumentNullException>(() =>
            CorsHandler.HandlePreflight(request, null!)
        );

        await Assert.That(ex.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task Preflight_carries_vary_origin()
    {
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, DefaultOptions);

        await Assert.That(response!.Headers[HttpHeaderNames.Vary]).IsEqualTo("Origin");
    }

    [Test]
    public async Task Preflight_403_carries_vary_origin()
    {
        // The rejection varies by origin too: a shared cache must not replay this 403
        // (or a later allow) to a different origin.
        var request = CreateRequest(
            "OPTIONS",
            new("Origin", "https://evil.com"),
            new("Host", "localhost")
        );

        var response = CorsHandler.HandlePreflight(request, DefaultOptions);

        await Assert.That(response!.StatusCode).IsEqualTo(403);
        await Assert.That(response.Headers[HttpHeaderNames.Vary]).IsEqualTo("Origin");
    }

    [Test]
    public async Task GetResponseHeaders_carries_vary_origin()
    {
        var request = CreateRequest(
            "GET",
            new("Origin", "https://example.com"),
            new("Host", "localhost")
        );

        var headers = CorsHandler.GetResponseHeaders(request, DefaultOptions);

        var vary = headers.FirstOrDefault(h => h.Key == HttpHeaderNames.Vary);
        await Assert.That(vary.Value).IsEqualTo("Origin");
    }

    [Test]
    public async Task WithVaryOrigin_merges_into_an_existing_vary_field()
    {
        HttpHeaderCollection existing = [new(HttpHeaderNames.Vary, "Accept-Encoding")];

        var merged = CorsHandler.WithVaryOrigin(existing);

        await Assert.That(merged[HttpHeaderNames.Vary]).IsEqualTo("Accept-Encoding, Origin");
        await Assert.That(merged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WithVaryOrigin_is_idempotent()
    {
        HttpHeaderCollection existing = [new(HttpHeaderNames.Vary, "Origin")];

        var merged = CorsHandler.WithVaryOrigin(existing);

        await Assert.That(merged[HttpHeaderNames.Vary]).IsEqualTo("Origin");
        await Assert.That(merged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WithVaryOrigin_folds_multiple_vary_fields_into_one()
    {
        HttpHeaderCollection existing =
        [
            new(HttpHeaderNames.Vary, "Accept-Encoding"),
            new(HttpHeaderNames.Vary, "Accept-Language"),
        ];

        var merged = CorsHandler.WithVaryOrigin(existing);

        await Assert
            .That(merged[HttpHeaderNames.Vary])
            .IsEqualTo("Accept-Encoding, Accept-Language, Origin");
        await Assert.That(merged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Null_origin_is_not_reflected()
    {
        // `Origin: null` is sent by sandboxed iframes and data: URLs; reflecting it
        // under a wildcard would hand every opaque origin a credentialed grant.
        var options = new CorsOptions { AllowedOrigins = ["*"] };
        var request = CreateRequest("OPTIONS", new("Origin", "null"), new("Host", "localhost"));

        var response = CorsHandler.HandlePreflight(request, options);

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(403);
        await Assert
            .That(response.Headers.TryGetValue("Access-Control-Allow-Origin", out _))
            .IsFalse();
    }

    [Test]
    public async Task GetResponseHeaders_ignores_null_origin()
    {
        var options = new CorsOptions { AllowedOrigins = ["*"] };
        var request = CreateRequest("GET", new("Origin", "null"), new("Host", "localhost"));

        var headers = CorsHandler.GetResponseHeaders(request, options);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Explicit_null_origin_entry_still_rejects_null()
    {
        // Listing the literal "null" must not re-enable the opaque-origin grant.
        var options = new CorsOptions { AllowedOrigins = ["null", "https://example.com"] };
        var request = CreateRequest("OPTIONS", new("Origin", "null"), new("Host", "localhost"));

        var response = CorsHandler.HandlePreflight(request, options);

        await Assert.That(response!.StatusCode).IsEqualTo(403);
    }

    [Test]
    public async Task HandlePreflight_includes_max_age_when_set()
    {
        var options = new CorsOptions { AllowedOrigins = ["https://example.com"], MaxAge = 3600 };
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
        var options = new CorsOptions { AllowedOrigins = ["*"] };
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
            AllowedOrigins = ["https://example.com"],
            ExposedHeaders = ["X-Custom", "X-Request-Id"],
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
            AllowedOrigins = ["https://example.com"],
            AllowedMethods = ["GET", "POST"],
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
