namespace PicoNode.Web.Tests;

public sealed class SetCookieBuilderTests
{
    [Test]
    public async Task SetCookieBuilder_builds_simple_cookie()
    {
        var header = new SetCookieBuilder("session", "abc123").Build();

        await Assert.That(header.Key).IsEqualTo("Set-Cookie");
        await Assert.That(header.Value).IsEqualTo("session=abc123");
    }

    [Test]
    public async Task SetCookieBuilder_builds_full_cookie()
    {
        var expires = new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero);
        var header = new SetCookieBuilder("token", "xyz")
            .Domain(".example.com")
            .Path("/api")
            .Expires(expires)
            .MaxAge(3600)
            .Secure()
            .HttpOnly()
            .SameSite("Strict")
            .Build();

        await Assert.That(header.Value).Contains("token=xyz");
        await Assert.That(header.Value).Contains("Domain=.example.com");
        await Assert.That(header.Value).Contains("Path=/api");
        await Assert.That(header.Value).Contains("Expires=Wed, 31 Dec 2025 23:59:59 GMT");
        await Assert.That(header.Value).Contains("Max-Age=3600");
        await Assert.That(header.Value).Contains("Secure");
        await Assert.That(header.Value).Contains("HttpOnly");
        await Assert.That(header.Value).Contains("SameSite=Strict");
    }

    [Test]
    public async Task SetCookieBuilder_supports_delete_via_max_age_zero()
    {
        var header = new SetCookieBuilder("session", "").MaxAge(0).Path("/").Build();

        await Assert.That(header.Value).IsEqualTo("session=; Path=/; Max-Age=0");
    }

    [Test]
    public async Task Cookie_name_with_semicolon_throws()
    {
        // `name=value` is concatenated too, so the name is the other half of the same
        // injection: an unvalidated name adds arbitrary attributes just as a value did.
        var ex = Assert.Throws<ArgumentException>(() =>
            new SetCookieBuilder("theme; Path=/admin", "x").Build()
        );

        await Assert.That(ex.Message).Contains("name");
    }

    [Test]
    public async Task Cookie_name_with_token_characters_is_allowed()
    {
        var header = new SetCookieBuilder("__Host-pn.1_session$x", "abc").Build();

        await Assert.That(header.Value).IsEqualTo("__Host-pn.1_session$x=abc");
    }

    [Test]
    public async Task Cookie_value_with_semicolon_throws()
    {
        // `;` separates attributes, so this value used to inject Path=/admin into the
        // serialized Set-Cookie line.
        var ex = Assert.Throws<ArgumentException>(() =>
            new SetCookieBuilder("theme", "x; Path=/admin").Build()
        );

        await Assert.That(ex.Message).Contains("cookie-octet");
    }

    [Test]
    public async Task Cookie_value_with_comma_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SetCookieBuilder("theme", "a,b").Build()
        );

        await Assert.That(ex.Message).Contains("cookie-octet");
    }

    [Test]
    public async Task Cookie_value_with_non_ascii_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SetCookieBuilder("theme", "théme").Build()
        );

        await Assert.That(ex.Message).Contains("cookie-octet");
    }

    [Test]
    public async Task Cookie_value_with_space_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SetCookieBuilder("theme", "dark mode").Build()
        );

        await Assert.That(ex.Message).Contains("cookie-octet");
    }

    [Test]
    public async Task Cookie_value_accepts_every_rfc6265_cookie_octet()
    {
        var allowed = new StringBuilder();
        for (var c = 0x21; c <= 0x7E; c++)
        {
            if (c is 0x22 or 0x2C or 0x3B or 0x5C)
                continue;
            allowed.Append((char)c);
        }

        var header = new SetCookieBuilder("blob", allowed.ToString()).Build();

        await Assert.That(header.Value).IsEqualTo($"blob={allowed}");
    }

    [Test]
    public async Task Cookie_value_may_be_empty()
    {
        var header = new SetCookieBuilder("session", "").Build();

        await Assert.That(header.Value).IsEqualTo("session=");
    }

    [Test]
    public async Task Cookie_same_site_with_unknown_value_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SetCookieBuilder("session", "abc").SameSite("Sometimes").Build()
        );

        await Assert.That(ex.Message).Contains("SameSite");
    }

    [Test]
    public async Task Cookie_same_site_accepts_none()
    {
        var header = new SetCookieBuilder("session", "abc").SameSite("None").Build();

        await Assert.That(header.Value).Contains("SameSite=None");
    }

    [Test]
    public async Task Cookie_domain_with_semicolon_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SetCookieBuilder("session", "abc").Domain("example.com; Path=/admin").Build()
        );

        await Assert.That(ex.Message).Contains("Domain");
    }

    [Test]
    public async Task Cookie_path_with_comma_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SetCookieBuilder("session", "abc").Path("/a,/b").Build()
        );

        await Assert.That(ex.Message).Contains("Path");
    }

    [Test]
    public async Task Cookie_domain_may_be_empty_for_host_only_cookies()
    {
        // RFC 6265 §4.1.2.3: an empty Domain attribute is undefined, and user agents
        // SHOULD ignore the attribute entirely — so an empty value stays legal (it is
        // neither a separator nor an injection).
        var header = new SetCookieBuilder("session", "abc").Domain("").Build();

        await Assert.That(header.Value).IsEqualTo("session=abc; Domain=");
    }
}
