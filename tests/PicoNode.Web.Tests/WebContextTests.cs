namespace PicoNode.Web.Tests;

public sealed class WebContextTests
{
    [Test]
    public async Task Create_parses_path_from_target()
    {
        var request = CreateRequest("GET", "/hello");
        var context = WebContext.Create(request);

        await Assert.That(context.Path).IsEqualTo("/hello");
        await Assert.That(context.QueryString).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Create_parses_path_and_query_string()
    {
        var request = CreateRequest("GET", "/search?q=pico&page=1");
        var context = WebContext.Create(request);

        await Assert.That(context.Path).IsEqualTo("/search");
        await Assert.That(context.QueryString).IsEqualTo("q=pico&page=1");
    }

    [Test]
    public async Task Query_parses_query_string_into_dictionary()
    {
        var request = CreateRequest("GET", "/search?q=pico&page=1");
        var context = WebContext.Create(request);

        await Assert.That(context.Query["q"]).IsEqualTo("pico");
        await Assert.That(context.Query["page"]).IsEqualTo("1");
    }

    [Test]
    public async Task Query_returns_empty_dictionary_when_no_query_string()
    {
        var request = CreateRequest("GET", "/hello");
        var context = WebContext.Create(request);

        await Assert.That(context.Query).IsEmpty();
    }

    [Test]
    public async Task Query_first_value_wins_for_duplicate_keys()
    {
        var request = CreateRequest("GET", "/search?q=first&q=second");
        var context = WebContext.Create(request);

        await Assert.That(context.Query["q"]).IsEqualTo("first");
    }

    [Test]
    public async Task RouteValues_returns_empty_by_default()
    {
        var request = CreateRequest("GET", "/hello");
        var context = WebContext.Create(request);

        await Assert.That(context.RouteValues).IsEmpty();
    }

    [Test]
    public async Task Request_is_preserved()
    {
        var request = CreateRequest("POST", "/echo");
        var context = WebContext.Create(request);

        await Assert.That(context.Request).IsEqualTo(request);
    }

    [Test]
    public async Task Query_decodes_percent_encoded_values()
    {
        var request = CreateRequest("GET", "/search?q=hello%20world&tag=%E4%B8%AD%E6%96%87");
        var context = WebContext.Create(request);

        await Assert.That(context.Query["q"]).IsEqualTo("hello world");
        await Assert.That(context.Query["tag"]).IsEqualTo("\u4e2d\u6587");
    }

    [Test]
    public async Task Query_decodes_percent_encoded_keys()
    {
        var request = CreateRequest("GET", "/search?hello%20key=value");
        var context = WebContext.Create(request);

        await Assert.That(context.Query["hello key"]).IsEqualTo("value");
    }

    private static HttpRequest CreateRequest(string method, string target) =>
        new() { Method = method, Target = target, };
}
