namespace PicoNode.Web.Tests;

public sealed class SessionIdDelegateTests
{
    [Test]
    public async Task Cookie_extract_reads_session_cookie()
    {
        var (extract, _) = SessionCookie.Create("sid");

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = "sid=abc123; other=value",
            },
        };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsEqualTo("abc123");
    }

    [Test]
    public async Task Cookie_extract_returns_null_when_no_cookie_header()
    {
        var (extract, _) = SessionCookie.Create("sid");

        var request = new HttpRequest { Method = "GET", Target = "/" };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsNull();
    }

    [Test]
    public async Task Cookie_extract_returns_null_when_cookie_not_found()
    {
        var (extract, _) = SessionCookie.Create("sid");

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = "other=value",
            },
        };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsNull();
    }

    [Test]
    public async Task Cookie_set_adds_set_cookie_header()
    {
        var (_, set) = SessionCookie.Create("sid");

        var response = new HttpResponse { StatusCode = 200 };
        set(response, "abc123");

        await Assert
            .That(response.Headers["Set-Cookie"])
            .IsEqualTo("sid=abc123; Path=/; HttpOnly; SameSite=Lax");
    }

    [Test]
    public async Task Cookie_set_uses_custom_cookie_name()
    {
        var (_, set) = SessionCookie.Create("mysession");

        var response = new HttpResponse { StatusCode = 200 };
        set(response, "xyz");

        await Assert
            .That(response.Headers["Set-Cookie"])
            .IsEqualTo("mysession=xyz; Path=/; HttpOnly; SameSite=Lax");
    }

    [Test]
    public async Task Header_extract_reads_custom_header()
    {
        var (extract, _) = SessionHeader.Create("X-Session-Id");

        var request = new HttpRequest
        {
            Method = "GET",
            Target = "/",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Session-Id"] = "abc123",
            },
        };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsEqualTo("abc123");
    }

    [Test]
    public async Task Header_extract_returns_null_when_header_absent()
    {
        var (extract, _) = SessionHeader.Create("X-Session-Id");

        var request = new HttpRequest { Method = "GET", Target = "/" };

        var sessionId = extract(request);

        await Assert.That(sessionId).IsNull();
    }

    [Test]
    public async Task Header_set_adds_response_header()
    {
        var (_, set) = SessionHeader.Create("X-Session-Id");

        var response = new HttpResponse { StatusCode = 200 };
        set(response, "abc123");

        await Assert.That(response.Headers["X-Session-Id"]).IsEqualTo("abc123");
    }

    [Test]
    public async Task Cookie_options_emit_secure_httponly_strict_cookie()
    {
        var (_, set) = SessionCookie.Create(
            new SessionCookieOptions
            {
                CookieName = "mysid",
                Path = "/app",
                Secure = true,
                HttpOnly = true,
                SameSite = "Strict",
            }
        );

        var response = new HttpResponse { StatusCode = 200 };
        set(response, "abc123");

        var cookie = response.Headers[HttpHeaderNames.SetCookie];
        await Assert.That(cookie).IsNotNull();
        await Assert.That(cookie!).StartsWith("mysid=abc123");
        await Assert.That(cookie).Contains("Path=/app");
        await Assert.That(cookie).Contains("Secure");
        await Assert.That(cookie).Contains("HttpOnly");
        await Assert.That(cookie).Contains("SameSite=Strict");
    }

    [Test]
    public async Task Cookie_default_options_omit_secure_and_keep_lax()
    {
        var (_, set) = SessionCookie.Create();

        var response = new HttpResponse { StatusCode = 200 };
        set(response, "abc123");

        var cookie = response.Headers[HttpHeaderNames.SetCookie];
        await Assert.That(cookie).IsNotNull();
        // Secure stays opt-in so plain-HTTP local development keeps working.
        await Assert.That(cookie!).DoesNotContain("Secure");
        await Assert.That(cookie).Contains("HttpOnly");
        await Assert.That(cookie).Contains("SameSite=Lax");
    }
}
