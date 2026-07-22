namespace PicoHtmx.Tests;

public sealed class ComponentsTests
{
    [Test]
    public async Task ListPage_ShowsEmptyMessage()
    {
        var html = Ux.ListPage("Sessions", Array.Empty<string>(), s => H.P(s));
        await Assert.That(html).Contains("No items");
    }

    [Test]
    public async Task ListPage_RendersItems()
    {
        var items = new[] { "a", "b" };
        var html = Ux.ListPage("Test", items, s => H.P(s));
        await Assert.That(html).Contains("<p>a</p>");
        await Assert.That(html).Contains("<p>b</p>");
    }

    [Test]
    public async Task ListPage_IncludesHeader()
    {
        var html = Ux.ListPage("MyList", new[] { "x" }, s => H.P(s));
        await Assert.That(html).Contains("MyList");
    }

    [Test]
    public async Task Form_IncludesAction()
    {
        var html = Ux.Form("/submit", "post");
        await Assert.That(html).Contains("action=\"/submit\"");
    }

    [Test]
    public async Task Form_IncludesMethod()
    {
        var html = Ux.Form("/s", "post");
        await Assert.That(html).Contains("method=\"post\"");
    }

    [Test]
    public async Task Form_ShowsError()
    {
        var html = Ux.Form("/s", "post", error: "Invalid input");
        await Assert.That(html).Contains("Invalid input");
    }

    [Test]
    public async Task FormField_RendersLabelAndInput()
    {
        var html = Ux.FormField("Name", "name");
        await Assert.That(html).Contains("<label");
        await Assert.That(html).Contains("name=\"name\"");
    }

    [Test]
    public async Task ConfirmDialog_ContainsConfirmUrl()
    {
        var html = Ux.ConfirmDialog("Delete?", "/delete/1");
        await Assert.That(html).Contains("hx-post=\"/delete/1\"");
    }

    [Test]
    public async Task Toast_HasCorrectType()
    {
        var html = Ux.Toast("Saved", isError: false);
        await Assert.That(html).Contains("class=\"toast success\"");
    }

    [Test]
    public async Task Toast_Error_HasErrorClass()
    {
        var html = Ux.Toast("Failed", isError: true);
        await Assert.That(html).Contains("class=\"toast error\"");
    }
}
