namespace PicoNode.Web.Tests;

public sealed class AuthMiddlewareTests
{
    [Test]
    public async Task Valid_bearer_token_injects_identity()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (token, ct) =>
                ValueTask.FromResult<AuthIdentity?>(
                    token == "valid-token"
                        ? new AuthIdentity { UserId = "user-1" }
                        : null),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer valid-token",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        var identity = AuthMiddleware.GetIdentity(context);
        await Assert.That(identity).IsNotNull();
        await Assert.That(identity!.UserId).IsEqualTo("user-1");
    }

    [Test]
    public async Task Missing_authorization_header_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                throw new InvalidOperationException("should not be called"),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest { Method = "GET", Target = "/" };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }

    [Test]
    public async Task Non_bearer_scheme_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                throw new InvalidOperationException("should not be called"),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Basic dXNlcjpwYXNz",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }

    [Test]
    public async Task ValidateToken_returns_null_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                ValueTask.FromResult<AuthIdentity?>(null),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer any-token",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }

    [Test]
    public async Task ValidateToken_throws_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                throw new InvalidOperationException("db down"),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer token",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (ctx, ct) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }

    [Test]
    public async Task Bearer_scheme_is_case_insensitive()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (token, ct) =>
                ValueTask.FromResult<AuthIdentity?>(
                    token == "tk" ? new AuthIdentity { UserId = "u" } : null),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "bearer tk",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)?.UserId).IsEqualTo("u");
    }

    [Test]
    public async Task Token_with_comma_truncates_at_first_comma()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (token, ct) =>
                ValueTask.FromResult<AuthIdentity?>(
                    token == "abc123" ? new AuthIdentity { UserId = "u" } : null),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer abc123, realm=\"example\"",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)?.UserId).IsEqualTo("u");
    }

    [Test]
    public async Task Empty_token_after_bearer_skips_auth()
    {
        var options = new AuthOptions
        {
            ValidateToken = static (_, _) =>
                throw new InvalidOperationException("should not be called"),
        };
        var middleware = AuthMiddleware.Create(options);

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer ",
            },
        };
        var context = WebContext.Create(request);

        await middleware(context, (_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }),
            CancellationToken.None);

        await Assert.That(AuthMiddleware.GetIdentity(context)).IsNull();
    }
}
