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
}
