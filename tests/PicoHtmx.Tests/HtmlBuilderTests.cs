namespace PicoHtmx.Tests;

public sealed class HtmlBuilderTests
{
    [Test]
    public async Task E_EncodesHtmlSpecials()
    {
        await Assert.That(H.E("<script>alert('xss')</script>"))
            .IsEqualTo("&lt;script&gt;alert('xss')&lt;/script&gt;");
    }

    [Test]
    public async Task E_DoesNotDoubleEncode()
    {
        await Assert.That(H.E("a &amp; b")).IsEqualTo("a &amp;amp; b");
    }

    [Test]
    public async Task E_ReturnsEmptyForNull()
    {
        await Assert.That(H.E(null)).IsEqualTo("");
    }

    [Test]
    public async Task Tag_SelfClosing_NoAttrs()
    {
        var result = H.Tag("br");
        await Assert.That(result).IsEqualTo("<br />");
    }

    [Test]
    public async Task Tag_WithContent()
    {
        var result = H.Tag("p", "hello");
        await Assert.That(result).IsEqualTo("<p>hello</p>");
    }

    [Test]
    public async Task Tag_WithAttributes()
    {
        var result = H.Tag("div", "text", new { @class = "foo", id = "bar" });
        await Assert.That(result).IsEqualTo("<div class=\"foo\" id=\"bar\">text</div>");
    }

    [Test]
    public async Task Tag_EncodesAttributeValues()
    {
        var result = H.Tag("div", "", new { title = "<evil>" });
        await Assert.That(result).IsEqualTo("<div title=\"&lt;evil&gt;\"></div>");
    }

    [Test]
    public async Task Div_NoAttrs()
    {
        await Assert.That(H.Div("hello")).IsEqualTo("<div>hello</div>");
    }

    [Test]
    public async Task Button_WithAttrs()
    {
        var result = H.Button("Click", new { type = "submit", @class = "btn" });
        await Assert.That(result).IsEqualTo("<button type=\"submit\" class=\"btn\">Click</button>");
    }

    [Test]
    public async Task Input_NoContent()
    {
        var result = H.Input(new { type = "text", name = "q" });
        await Assert.That(result).IsEqualTo("<input type=\"text\" name=\"q\" />");
    }

    [Test]
    public async Task A_WithHref()
    {
        var result = H.A("link", "/foo", new { @class = "nav" });
        await Assert.That(result).IsEqualTo("<a href=\"/foo\" class=\"nav\">link</a>");
    }
}
