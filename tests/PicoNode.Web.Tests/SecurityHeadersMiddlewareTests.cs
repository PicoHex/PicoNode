namespace PicoNode.Web.Tests;

public sealed class SecurityHeadersMiddlewareTests
{
    [Test]
    public async Task Injects_XContentTypeOptions_nosniff()
    {
        var middleware = new SecurityHeadersMiddleware();
        var context = CreateContext("GET", "/");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }),
            CancellationToken.None
        );

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(GetHeader(response, "X-Content-Type-Options")).IsEqualTo("nosniff");
    }

    [Test]
    public async Task Injects_XFrameOptions_deny()
    {
        var middleware = new SecurityHeadersMiddleware();
        var context = CreateContext("GET", "/");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }),
            CancellationToken.None
        );

        await Assert.That(GetHeader(response, "X-Frame-Options")).IsEqualTo("DENY");
    }

    [Test]
    public async Task Does_not_overwrite_existing_security_headers()
    {
        var middleware = new SecurityHeadersMiddleware();
        var context = CreateContext("GET", "/");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) =>
                ValueTask.FromResult(
                    new HttpResponse
                    {
                        StatusCode = 200,
                        ReasonPhrase = "OK",
                        Headers =
                        [
                            new KeyValuePair<string, string>("X-Content-Type-Options", "custom"),
                        ],
                    }
                ),
            CancellationToken.None
        );

        // should not overwrite application-set header
        await Assert.That(GetHeader(response, "X-Content-Type-Options")).IsEqualTo("custom");
    }

    [Test]
    public async Task Injects_StrictTransportSecurity_for_non_localhost()
    {
        var middleware = new SecurityHeadersMiddleware();
        var context = CreateContext("GET", "/");

        var response = await middleware.InvokeAsync(
            context,
            static (_, _) =>
                ValueTask.FromResult(new HttpResponse { StatusCode = 200, ReasonPhrase = "OK" }),
            CancellationToken.None
        );

        // HSTS should be present for any response
        await Assert
            .That(response.Headers.TryGetValue("Strict-Transport-Security", out _))
            .IsTrue();
    }

    private static WebContext CreateContext(string method, string path)
    {
        return WebContext.Create(
            new HttpRequest
            {
                Method = method,
                Target = path,
                HeaderFields = [],
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            }
        );
    }

    private static string? GetHeader(HttpResponse response, string name)
    {
        foreach (var header in response.Headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }
        return null;
    }
}
