namespace PicoNode.Web.Tests;

public sealed class IWebResultTests
{
    private static HttpRequest CreateRequest() => new()
    {
        Method = "GET",
        Target = "/",
        Path = "/",
    };

    [Test]
    public async Task EmptyResult_Returns204()
    {
        var ctx = WebContext.Create(CreateRequest());
        var result = new EmptyResult(204);

        var response = result.Execute(ctx);

        await Assert.That(response.StatusCode).IsEqualTo(204);
        await Assert.That(response.Body.IsEmpty).IsTrue();
    }

    [Test]
    public async Task HtmlResult_ReturnsHtmlWithContentType()
    {
        var ctx = WebContext.Create(CreateRequest());
        var result = new HtmlResult("<p>hello</p>");

        var response = result.Execute(ctx);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers).Contains(
            new KeyValuePair<string, string>("Content-Type", "text/html; charset=utf-8"));
        var body = Encoding.UTF8.GetString(response.Body.Span);
        await Assert.That(body).IsEqualTo("<p>hello</p>");
    }

    [Test]
    public async Task RedirectResult_Returns302WithLocation()
    {
        var ctx = WebContext.Create(CreateRequest());
        var result = new RedirectResult("/login");

        var response = result.Execute(ctx);

        await Assert.That(response.StatusCode).IsEqualTo(302);
        await Assert.That(response.Headers).Contains(
            new KeyValuePair<string, string>("Location", "/login"));
    }

    [Test]
    public async Task RedirectResult_Permanent_Returns301()
    {
        var ctx = WebContext.Create(CreateRequest());
        var result = new RedirectResult("/new-home", permanent: true);
        var response = result.Execute(ctx);

        await Assert.That(response.StatusCode).IsEqualTo(301);
    }

    [Test]
    public async Task TextResult_ReturnsPlainText()
    {
        var ctx = WebContext.Create(CreateRequest());
        var result = new TextResult("hello world");

        var response = result.Execute(ctx);

        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.Headers).Contains(
            new KeyValuePair<string, string>("Content-Type", "text/plain; charset=utf-8"));
    }
}
