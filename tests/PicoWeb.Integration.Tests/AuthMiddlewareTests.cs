namespace PicoWeb.Integration.Tests;

/// <summary>
/// Integration tests for AuthMiddleware (Bearer token authentication).
/// </summary>
public sealed class AuthMiddlewareTests
{
    // ── Helpers ────────────────────────────────────────────────────────

    private static WebMiddleware CreateAuth(AuthOptions? options = null) =>
        AuthMiddleware.Create(
            options
                ?? new AuthOptions
                {
                    ValidateToken = static (token, ct) =>
                        ValueTask.FromResult<AuthIdentity?>(
                            token.StartsWith("demo-", StringComparison.Ordinal)
                                ? new AuthIdentity { UserId = token.Replace("demo-", "") }
                                : null
                        ),
                }
        );

    // ── Success cases ──────────────────────────────────────────────────

    [Test]
    public async Task Valid_bearer_token_injects_identity()
    {
        var middleware = CreateAuth();
        var request = new PicoNode.Http.HttpRequest
        {
            Method = "GET",
            Target = "/api/me",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer demo-alice",
            },
        };
        var context = WebContext.Create(request);

        AuthIdentity? seen = null;
        await middleware(
            context,
            (ctx, ct) =>
            {
                seen = AuthMiddleware.GetIdentity(ctx);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            },
            CancellationToken.None
        );

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.UserId).IsEqualTo("alice");
    }

    [Test]
    public async Task Valid_bearer_token_with_extra_whitespace()
    {
        var middleware = CreateAuth();
        var request = new PicoNode.Http.HttpRequest
        {
            Method = "GET",
            Target = "/api/me",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "  Bearer demo-alice  ",
            },
        };
        var context = WebContext.Create(request);

        AuthIdentity? seen = null;
        await middleware(
            context,
            (ctx, ct) =>
            {
                seen = AuthMiddleware.GetIdentity(ctx);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            },
            CancellationToken.None
        );

        // Split(" ", 2) gives ["", "Bearer"]
        // string.Equals(parts[0], "Bearer", OrdinalIgnoreCase) → false for ""
        await Assert.That(seen).IsNull();
    }

    // ── Failure / edge cases ───────────────────────────────────────────

    [Test]
    public async Task No_authorization_header_identity_is_null()
    {
        var middleware = CreateAuth();
        var request = new PicoNode.Http.HttpRequest { Method = "GET", Target = "/api/me" };
        var context = WebContext.Create(request);

        AuthIdentity? seen = null;
        await middleware(
            context,
            (ctx, ct) =>
            {
                seen = AuthMiddleware.GetIdentity(ctx);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            },
            CancellationToken.None
        );

        await Assert.That(seen).IsNull();
    }

    [Test]
    public async Task Invalid_token_identity_is_null()
    {
        var middleware = CreateAuth();
        var request = new PicoNode.Http.HttpRequest
        {
            Method = "GET",
            Target = "/api/me",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer bad-token",
            },
        };
        var context = WebContext.Create(request);

        AuthIdentity? seen = null;
        await middleware(
            context,
            (ctx, ct) =>
            {
                seen = AuthMiddleware.GetIdentity(ctx);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            },
            CancellationToken.None
        );

        await Assert.That(seen).IsNull();
    }

    [Test]
    public async Task Non_bearer_scheme_ignored()
    {
        var middleware = CreateAuth();
        var request = new PicoNode.Http.HttpRequest
        {
            Method = "GET",
            Target = "/api/me",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Basic ZGVtbzoxMjM=",
            },
        };
        var context = WebContext.Create(request);

        AuthIdentity? seen = null;
        await middleware(
            context,
            (ctx, ct) =>
            {
                seen = AuthMiddleware.GetIdentity(ctx);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            },
            CancellationToken.None
        );

        await Assert.That(seen).IsNull();
    }

    [Test]
    public async Task Empty_token_identity_is_null()
    {
        var middleware = CreateAuth();
        var request = new PicoNode.Http.HttpRequest
        {
            Method = "GET",
            Target = "/api/me",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer ",
            },
        };
        var context = WebContext.Create(request);

        AuthIdentity? seen = null;
        await middleware(
            context,
            (ctx, ct) =>
            {
                seen = AuthMiddleware.GetIdentity(ctx);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            },
            CancellationToken.None
        );

        await Assert.That(seen).IsNull();
    }

    [Test]
    public async Task Multi_value_authorization_extracts_first_token()
    {
        var middleware = CreateAuth();
        var request = new PicoNode.Http.HttpRequest
        {
            Method = "GET",
            Target = "/api/me",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer demo-alice, Bearer demo-bob",
            },
        };
        var context = WebContext.Create(request);

        AuthIdentity? seen = null;
        await middleware(
            context,
            (ctx, ct) =>
            {
                seen = AuthMiddleware.GetIdentity(ctx);
                return ValueTask.FromResult(WebResults.Text(200, "ok"));
            },
            CancellationToken.None
        );

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.UserId).IsEqualTo("alice");
    }

    [Test]
    public async Task ValidateToken_exception_returns_null_identity()
    {
        var throwing = new AuthOptions
        {
            ValidateToken = (token, ct) => throw new InvalidOperationException("broken"),
        };
        var middleware = AuthMiddleware.Create(throwing);
        var request = new PicoNode.Http.HttpRequest
        {
            Method = "GET",
            Target = "/api/me",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer anything",
            },
        };
        var context = WebContext.Create(request);

        AuthIdentity? seen = null;
        HttpResponse? response = null;
        try
        {
            response = await middleware(
                context,
                (ctx, ct) =>
                {
                    seen = AuthMiddleware.GetIdentity(ctx);
                    return ValueTask.FromResult(WebResults.Text(200, "ok"));
                },
                CancellationToken.None
            );
        }
        catch
        { /* middleware swallows ValidateToken exceptions */
        }

        await Assert.That(response).IsNotNull();
        await Assert.That(response!.StatusCode).IsEqualTo(200);
        await Assert.That(seen).IsNull();
    }

    [Test]
    public async Task Identity_is_isolated_between_contexts()
    {
        var middleware = CreateAuth();
        var ctx1 = WebContext.Create(
            new PicoNode.Http.HttpRequest
            {
                Method = "GET",
                Target = "/a",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer demo-alice",
                },
            }
        );
        var ctx2 = WebContext.Create(
            new PicoNode.Http.HttpRequest { Method = "GET", Target = "/b" }
        );

        await middleware(
            ctx1,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );
        await middleware(
            ctx2,
            (_, _) => ValueTask.FromResult(WebResults.Text(200, "ok")),
            CancellationToken.None
        );

        await Assert.That(AuthMiddleware.GetIdentity(ctx1)).IsNotNull();
        await Assert.That(AuthMiddleware.GetIdentity(ctx2)).IsNull();
    }
}
