namespace PicoNode.Http.Tests;

public sealed class CookieTests
{
    [Test]
    public async Task Parse_extracts_single_cookie()
    {
        var cookies = CookieParser.Parse("session=abc123");

        await Assert.That(cookies["session"]).IsEqualTo("abc123");
    }

    [Test]
    public async Task Parse_extracts_multiple_cookies()
    {
        var cookies = CookieParser.Parse("session=abc; theme=dark; lang=en");

        await Assert.That(cookies.Count).IsEqualTo(3);
        await Assert.That(cookies["session"]).IsEqualTo("abc");
        await Assert.That(cookies["theme"]).IsEqualTo("dark");
        await Assert.That(cookies["lang"]).IsEqualTo("en");
    }

    [Test]
    public async Task Parse_trims_whitespace()
    {
        var cookies = CookieParser.Parse("  name  =  value  ;  other  =  val  ");

        await Assert.That(cookies["name"]).IsEqualTo("value");
        await Assert.That(cookies["other"]).IsEqualTo("val");
    }

    [Test]
    public async Task Parse_first_value_wins_for_duplicates()
    {
        var cookies = CookieParser.Parse("id=first; id=second");

        await Assert.That(cookies["id"]).IsEqualTo("first");
    }

    [Test]
    public async Task Parse_from_headers_collects_cookie_headers()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Host", "example.com"),
            new("Cookie", "a=1; b=2"),
            new("Accept", "text/html"),
            new("Cookie", "c=3"),
        };

        var cookies = CookieParser.Parse(headers);

        await Assert.That(cookies.Count).IsEqualTo(3);
        await Assert.That(cookies["a"]).IsEqualTo("1");
        await Assert.That(cookies["c"]).IsEqualTo("3");
    }

    [Test]
    public async Task Parse_returns_empty_for_no_cookies()
    {
        var cookies = CookieParser.Parse("");

        await Assert.That(cookies.Count).IsEqualTo(0);
    }

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
}
