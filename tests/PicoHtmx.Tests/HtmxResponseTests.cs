namespace PicoHtmx.Tests;

public sealed class HtmxResponseTests
{
    [Test]
    public async Task Html_SetsContentType()
    {
        var resp = Htmx.Html("<div>hello</div>");
        await Assert.That(resp.Headers["Content-Type"]).IsEqualTo("text/html; charset=utf-8");
    }

    [Test]
    public async Task Html_SetsBody()
    {
        var resp = Htmx.Html("<p>test</p>");
        var body = Encoding.UTF8.GetString(resp.Body.ToArray());
        await Assert.That(body).IsEqualTo("<p>test</p>");
    }

    [Test]
    public async Task Html_DefaultStatusCodeIs200()
    {
        var resp = Htmx.Html("ok");
        await Assert.That(resp.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Html_CustomStatusCode()
    {
        var resp = Htmx.Html("not found", 404);
        await Assert.That(resp.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task SseHtml_SetsSseContentType()
    {
        var resp = Htmx.SseHtml("<div>stream</div>");
        await Assert.That(resp.Headers["Content-Type"]).IsEqualTo("text/event-stream");
    }

    [Test]
    public async Task Redirect_SetsHxRedirectHeader()
    {
        var resp = Htmx.Redirect("/sessions");
        await Assert.That(resp.Headers["HX-Redirect"]).IsEqualTo("/sessions");
    }

    [Test]
    public async Task Refresh_SetsHxRefreshHeader()
    {
        var resp = Htmx.Refresh();
        await Assert.That(resp.Headers["HX-Refresh"]).IsEqualTo("true");
    }

    [Test]
    public async Task Trigger_SetsHxTriggerHeader()
    {
        var resp = Htmx.Trigger("sessionCreated", "{id:1}");
        await Assert.That(resp.Headers["HX-Trigger"]).IsEqualTo("{id:1}");
    }

    [Test]
    public async Task Ok_Returns200WithOkBody()
    {
        var resp = Htmx.Ok();
        await Assert.That(resp.StatusCode).IsEqualTo(200);
        var body = Encoding.UTF8.GetString(resp.Body.ToArray());
        await Assert.That(body).IsEqualTo("ok");
    }
}
