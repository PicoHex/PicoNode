namespace PicoHtmx.Tests;

public sealed class LayoutTests
{
    [Test]
    public async Task Page_ContainsDoctype()
    {
        var html = Layout.Page("Test", "<p>body</p>");
        await Assert.That(html).StartsWith("<!DOCTYPE html>");
    }

    [Test]
    public async Task Page_IncludesTitle()
    {
        var html = Layout.Page("MyApp", "<p>body</p>");
        await Assert.That(html).Contains("<title>MyApp</title>");
    }

    [Test]
    public async Task Page_IncludesHtmxScript()
    {
        var html = Layout.Page("Test", "<p>body</p>");
        await Assert.That(html).Contains("htmx.org");
    }

    [Test]
    public async Task Page_IncludesBody()
    {
        var html = Layout.Page("Test", "<p>hello</p>");
        await Assert.That(html).Contains("<p>hello</p>");
    }

    [Test]
    public async Task Page_IncludesCustomStyles()
    {
        var html = Layout.Page("Test", "<p>body</p>", styles: "/app.css");
        await Assert.That(html).Contains("href=\"/app.css\"");
    }

    [Test]
    public async Task NavItem_Active_AddsActiveClass()
    {
        var item = Layout.NavItem("Sessions", "/sessions", active: true);
        await Assert.That(item).Contains("class=\"active\"");
    }

    [Test]
    public async Task NavItem_Inactive_DoesNotHaveActiveClass()
    {
        var item = Layout.NavItem("Agents", "/agents", active: false);
        await Assert.That(item).DoesNotContain("active");
    }
}
